using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Identity;

/// <summary>
/// User accounts and permission checks. Every method runs inside one tenant's transaction,
/// so row-level security limits it to that tenant's rows.
/// </summary>
public sealed class UserRepository(Func<Task<NpgsqlConnection>> openConnection, PasswordService passwords)
{
    public async Task<Guid> CreateActiveUserAsync(
        Guid tenantId,
        string email,
        string fullName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var userId = Guid.NewGuid();
        var passwordHash = passwords.Hash(password);

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO users (id, tenant_id, email, full_name, password_hash, status)
            VALUES (@id, @tenant_id, @email, @full_name, @password_hash, 'active')
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("email", normalizedEmail);
        command.Parameters.AddWithValue("full_name", fullName);
        command.Parameters.AddWithValue("password_hash", passwordHash);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return userId;
    }

    /// <summary>
    /// Returns the user id when the email and password match an active account in this tenant.
    /// Returns null otherwise, with the same result for unknown emails and wrong passwords.
    /// </summary>
    public async Task<Guid?> AuthenticateAsync(
        Guid tenantId,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT id, password_hash, status FROM users WHERE email = @email",
            connection,
            transaction);
        command.Parameters.AddWithValue("email", normalizedEmail);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var userId = reader.GetGuid(0);
        var passwordHash = reader.IsDBNull(1) ? null : reader.GetString(1);
        var status = reader.GetString(2);

        if (passwordHash is null || status != "active")
        {
            return null;
        }

        return passwords.Verify(passwordHash, password) ? userId : null;
    }

    /// <summary>
    /// True when an active user holds a role in this tenant that grants the permission.
    /// Permissions come only through roles. There is no direct user grant.
    /// </summary>
    public async Task<bool> HasPermissionAsync(
        Guid tenantId,
        Guid userId,
        string permissionKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1
              FROM user_roles ur
              JOIN role_permissions rp ON rp.role_id = ur.role_id AND rp.tenant_id = ur.tenant_id
              JOIN users u ON u.id = ur.user_id AND u.tenant_id = ur.tenant_id
              WHERE ur.user_id = @user_id
                AND rp.permission_key = @permission_key
                AND u.status = 'active')
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("permission_key", permissionKey);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Creates an account with a username and password, and no email. Usernames are lower case,
    /// 3 to 32 characters, letters, digits, dot, underscore or hyphen, and unique within the company.
    /// </summary>
    public async Task<Guid> CreateUsernameUserAsync(
        Guid tenantId,
        string username,
        string fullName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var userId = Guid.NewGuid();
        var passwordHash = passwords.Hash(password);

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO users (id, tenant_id, username, full_name, password_hash, status)
            VALUES (@id, @tenant_id, @username, @full_name, @password_hash, 'active')
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("username", normalizedUsername);
        command.Parameters.AddWithValue("full_name", fullName);
        command.Parameters.AddWithValue("password_hash", passwordHash);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return userId;
    }

    /// <summary>
    /// Returns the user id when the username and password match an active account in this company.
    /// Accounts without a password (Gmail-only) never match. Unknown usernames and wrong passwords
    /// return the same result.
    /// </summary>
    public async Task<Guid?> AuthenticateWithUsernameAsync(
        Guid tenantId,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!UsernameRules.IsValid(username))
        {
            return null;
        }

        var normalizedUsername = NormalizeUsername(username);

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT id, password_hash, status FROM users WHERE username = @username",
            connection,
            transaction);
        command.Parameters.AddWithValue("username", normalizedUsername);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var userId = reader.GetGuid(0);
        var passwordHash = reader.IsDBNull(1) ? null : reader.GetString(1);
        var status = reader.GetString(2);

        if (passwordHash is null || status != "active")
        {
            return null;
        }

        return passwords.Verify(passwordHash, password) ? userId : null;
    }

    internal static string NormalizeUsername(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var normalized = username.Trim().ToLowerInvariant();
        if (!UsernameRules.IsValid(normalized))
        {
            throw new ArgumentException(
                "Username must be 3 to 32 characters: letters, digits, dot, underscore or hyphen, starting with a letter or digit.",
                nameof(username));
        }

        return normalized;
    }

    internal static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var normalized = email.Trim().ToLowerInvariant();
        if (!normalized.Contains('@'))
        {
            throw new ArgumentException("Email address is not valid.", nameof(email));
        }

        return normalized;
    }
}
