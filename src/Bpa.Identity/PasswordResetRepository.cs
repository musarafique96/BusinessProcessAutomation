using System.Security.Cryptography;
using System.Text;
using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Identity;

/// <summary>The account has permissions the person resetting its password doesn't hold.</summary>
public sealed class AccountExceedsRequesterException()
    : Exception("That account has permissions you don't have, so you can't reset its password.");

public sealed record CreatedPasswordReset(Guid Id, string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Admin-issued password resets. An admin with users.manage creates a one-time link for an account,
/// and the person sets a new password with it. Only a hash of the token is stored.
/// </summary>
public sealed class PasswordResetRepository(Func<Task<NpgsqlConnection>> openConnection, PasswordService passwords)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Creates a reset for an account. Any earlier open reset for that account stops working.
    /// Throws AccountExceedsRequesterException when the account holds a permission the requester doesn't.
    /// </summary>
    public async Task<CreatedPasswordReset> CreateAsync(
        Guid tenantId,
        Guid requestedBy,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var resetId = Guid.NewGuid();
        var token = NewToken();
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using (var check = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1 FROM user_roles ur
              JOIN role_permissions rp ON rp.role_id = ur.role_id AND rp.tenant_id = ur.tenant_id
              WHERE ur.user_id = @user_id AND ur.tenant_id = @tenant_id
                AND rp.permission_key NOT IN (
                  SELECT rp2.permission_key
                  FROM user_roles mine
                  JOIN role_permissions rp2 ON rp2.role_id = mine.role_id AND rp2.tenant_id = mine.tenant_id
                  WHERE mine.user_id = @requested_by AND mine.tenant_id = @tenant_id))
            """,
            connection,
            transaction))
        {
            check.Parameters.AddWithValue("user_id", userId);
            check.Parameters.AddWithValue("requested_by", requestedBy);
            check.Parameters.AddWithValue("tenant_id", tenantId);
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new AccountExceedsRequesterException();
            }
        }

        await using (var stopOld = new NpgsqlCommand(
            "UPDATE password_resets SET used_at = now() WHERE user_id = @user_id AND used_at IS NULL",
            connection,
            transaction))
        {
            stopOld.Parameters.AddWithValue("user_id", userId);
            await stopOld.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO password_resets (id, tenant_id, user_id, requested_by, token_hash, expires_at)
            VALUES (@id, @tenant_id, @user_id, @requested_by, @token_hash, @expires_at)
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("id", resetId);
            insert.Parameters.AddWithValue("tenant_id", tenantId);
            insert.Parameters.AddWithValue("user_id", userId);
            insert.Parameters.AddWithValue("requested_by", requestedBy);
            insert.Parameters.AddWithValue("token_hash", HashToken(token));
            insert.Parameters.AddWithValue("expires_at", expiresAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreatedPasswordReset(resetId, token, expiresAt);
    }

    /// <summary>
    /// Sets a new password with a valid reset link. Returns the account id, or null when the link is
    /// unknown, expired, or already used. Throws ArgumentException when the password is too short.
    /// </summary>
    public async Task<Guid?> CompleteAsync(
        Guid tenantId,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var passwordHash = passwords.Hash(newPassword);
        var tokenHash = HashToken(token);

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        Guid resetId;
        Guid userId;

        // Lock the reset so the same link can't be used twice at once.
        await using (var find = new NpgsqlCommand(
            """
            SELECT id, user_id FROM password_resets
            WHERE token_hash = @token_hash AND used_at IS NULL AND expires_at > now()
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

            resetId = reader.GetGuid(0);
            userId = reader.GetGuid(1);
        }

        await using (var setPassword = new NpgsqlCommand(
            "UPDATE users SET password_hash = @password_hash WHERE id = @user_id AND status = 'active'",
            connection,
            transaction))
        {
            setPassword.Parameters.AddWithValue("password_hash", passwordHash);
            setPassword.Parameters.AddWithValue("user_id", userId);
            if (await setPassword.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return null;
            }
        }

        await using (var markUsed = new NpgsqlCommand(
            "UPDATE password_resets SET used_at = now() WHERE id = @id",
            connection,
            transaction))
        {
            markUsed.Parameters.AddWithValue("id", resetId);
            await markUsed.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return userId;
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
