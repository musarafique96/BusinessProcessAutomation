using System.Security.Cryptography;
using System.Text;
using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Identity;

public sealed record CreatedInvitation(Guid Id, string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Invitations: an admin invites a person by email or username with a role, and the person accepts
/// by setting a password. The token is shown once, and only its SHA-256 hash is stored.
/// Every method runs inside one company's transaction, so row-level security applies.
/// </summary>
public sealed class InvitationRepository(Func<Task<NpgsqlConnection>> openConnection, PasswordService passwords)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Creates an invitation for exactly one of email or username. Throws ArgumentException when both
    /// or neither are given, or when the value is malformed.
    /// </summary>
    public async Task<CreatedInvitation> CreateAsync(
        Guid tenantId,
        Guid invitedBy,
        string fullName,
        Guid roleId,
        string? email,
        string? username,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        var (normalizedEmail, normalizedUsername) = NormalizeIdentity(email, username);

        var invitationId = Guid.NewGuid();
        var token = NewToken();
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO invitations (id, tenant_id, email, username, full_name, role_id, token_hash, invited_by, expires_at)
            VALUES (@id, @tenant_id, @email, @username, @full_name, @role_id, @token_hash, @invited_by, @expires_at)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", invitationId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("email", (object?)normalizedEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("username", (object?)normalizedUsername ?? DBNull.Value);
        command.Parameters.AddWithValue("full_name", fullName.Trim());
        command.Parameters.AddWithValue("role_id", roleId);
        command.Parameters.AddWithValue("token_hash", HashToken(token));
        command.Parameters.AddWithValue("invited_by", invitedBy);
        command.Parameters.AddWithValue("expires_at", expiresAt);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedInvitation(invitationId, token, expiresAt);
    }

    /// <summary>
    /// Cancels a pending invitation. Returns false when it doesn't exist, was already accepted,
    /// or was already revoked.
    /// </summary>
    public async Task<bool> RevokeAsync(Guid tenantId, Guid invitationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            UPDATE invitations SET revoked_at = now()
            WHERE id = @id AND accepted_at IS NULL AND revoked_at IS NULL
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", invitationId);

        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed == 1;
    }

    /// <summary>
    /// Accepts a pending invitation: creates the account with the password and the invited role, and
    /// marks the invitation used. Returns the new user id, or null when the token is unknown, expired,
    /// revoked, or already used. Throws ArgumentException when the password is too short.
    /// </summary>
    public async Task<Guid?> AcceptAsync(
        Guid tenantId,
        string token,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var passwordHash = passwords.Hash(password);
        var tokenHash = HashToken(token);

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        Guid invitationId;
        Guid roleId;
        string? email;
        string? username;
        string fullName;

        // Lock the invitation so two accept requests with the same token can't both create an account.
        await using (var find = new NpgsqlCommand(
            """
            SELECT id, role_id, email, username, full_name
            FROM invitations
            WHERE token_hash = @token_hash
              AND accepted_at IS NULL
              AND revoked_at IS NULL
              AND expires_at > now()
            FOR UPDATE
            """,
            connection,
            transaction))
        {
            find.Parameters.AddWithValue("token_hash", tokenHash);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            invitationId = reader.GetGuid(0);
            roleId = reader.GetGuid(1);
            email = reader.IsDBNull(2) ? null : reader.GetString(2);
            username = reader.IsDBNull(3) ? null : reader.GetString(3);
            fullName = reader.GetString(4);
        }

        var userId = Guid.NewGuid();

        await using (var insertUser = new NpgsqlCommand(
            """
            INSERT INTO users (id, tenant_id, email, username, full_name, password_hash, status)
            VALUES (@id, @tenant_id, @email, @username, @full_name, @password_hash, 'active')
            """,
            connection,
            transaction))
        {
            insertUser.Parameters.AddWithValue("id", userId);
            insertUser.Parameters.AddWithValue("tenant_id", tenantId);
            insertUser.Parameters.AddWithValue("email", (object?)email ?? DBNull.Value);
            insertUser.Parameters.AddWithValue("username", (object?)username ?? DBNull.Value);
            insertUser.Parameters.AddWithValue("full_name", fullName);
            insertUser.Parameters.AddWithValue("password_hash", passwordHash);
            await insertUser.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var grantRole = new NpgsqlCommand(
            "INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@tenant_id, @user_id, @role_id)",
            connection,
            transaction))
        {
            grantRole.Parameters.AddWithValue("tenant_id", tenantId);
            grantRole.Parameters.AddWithValue("user_id", userId);
            grantRole.Parameters.AddWithValue("role_id", roleId);
            await grantRole.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var markAccepted = new NpgsqlCommand(
            "UPDATE invitations SET accepted_at = now(), accepted_user_id = @user_id WHERE id = @id",
            connection,
            transaction))
        {
            markAccepted.Parameters.AddWithValue("user_id", userId);
            markAccepted.Parameters.AddWithValue("id", invitationId);
            await markAccepted.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return userId;
    }

    private static (string? Email, string? Username) NormalizeIdentity(string? email, string? username)
    {
        var hasEmail = !string.IsNullOrWhiteSpace(email);
        var hasUsername = !string.IsNullOrWhiteSpace(username);
        if (hasEmail == hasUsername)
        {
            throw new ArgumentException("Give exactly one of email or username.");
        }

        return hasEmail
            ? (UserRepository.NormalizeEmail(email!), null)
            : (null, UserRepository.NormalizeUsername(username!));
    }

    // 32 random bytes, URL-safe base64. About 256 bits, so guessing a token is not practical.
    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
