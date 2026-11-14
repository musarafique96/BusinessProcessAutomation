using System.Text.RegularExpressions;
using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Identity;

/// <summary>The role has permissions the requester doesn't hold, so the requester can't create, edit, delete or give it.</summary>
public sealed class RoleExceedsRequesterException()
    : Exception("That role has permissions you don't have, so you can't change it.");

/// <summary>The person already holds permissions the requester doesn't have, so the requester can't change their roles.</summary>
public sealed class AccountRolesExceedRequesterException()
    : Exception("That person has permissions you don't have, so you can't change their roles.");

public sealed class RoleKeyTakenException() : Exception("A role with that key already exists in this company.");

public sealed class RoleNotFoundException() : Exception("That role was not found.");

public sealed class UserNotFoundException() : Exception("That person was not found.");

public sealed class UnknownPermissionException(string key) : Exception($"Unknown permission: {key}.");

public sealed record PermissionInfo(string Key, string Description);

public sealed record RoleInfo(Guid Id, string Key, string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// Roles, the permission catalogue, and who holds which role. Every change is limited to permissions the
/// requester holds. That check runs in the same transaction as the change, so it can't be skipped.
/// </summary>
public sealed class RoleRepository(Func<Task<NpgsqlConnection>> openConnection)
{
    private static readonly Regex KeyFormat = new("^[a-z][a-z0-9_]{2,39}$", RegexOptions.CultureInvariant);

    /// <summary>The fixed list of permissions the system can grant. It is the same for every company.</summary>
    public async Task<IReadOnlyList<PermissionInfo>> GetPermissionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await openConnection();
        await using var command = new NpgsqlCommand("SELECT key, description FROM permissions ORDER BY key", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var permissions = new List<PermissionInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            permissions.Add(new PermissionInfo(reader.GetString(0), reader.GetString(1)));
        }

        return permissions;
    }

    /// <summary>The roles of one company, with their permissions. Other companies' roles are never returned.</summary>
    public async Task<IReadOnlyList<RoleInfo>> ListRolesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT r.id, r.key, r.name,
                   COALESCE(array_agg(rp.permission_key ORDER BY rp.permission_key)
                            FILTER (WHERE rp.permission_key IS NOT NULL), '{}')
            FROM roles r
            LEFT JOIN role_permissions rp ON rp.role_id = r.id AND rp.tenant_id = r.tenant_id
            GROUP BY r.id, r.key, r.name
            ORDER BY r.key
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var roles = new List<RoleInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new RoleInfo(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<string[]>(3)));
        }

        return roles;
    }

    /// <summary>Creates a role. The requester must hold every permission the role includes.</summary>
    public async Task<Guid> CreateAsync(
        Guid tenantId,
        Guid requesterId,
        string key,
        string name,
        IEnumerable<string> permissionKeys,
        CancellationToken cancellationToken = default)
    {
        if (!KeyFormat.IsMatch(key))
        {
            throw new ArgumentException(
                "A role key is 3 to 40 characters: lower case letters, digits and underscores, starting with a letter.",
                nameof(key));
        }

        var roleName = CleanName(name);
        var permissions = permissionKeys.Distinct().ToArray();

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await RequireKnownPermissionsAsync(connection, transaction, permissions, cancellationToken);
        var held = await HeldPermissionsAsync(connection, transaction, tenantId, requesterId, cancellationToken);
        if (!held.IsSupersetOf(permissions))
        {
            throw new RoleExceedsRequesterException();
        }

        var roleId = Guid.NewGuid();
        try
        {
            await using var insertRole = new NpgsqlCommand(
                "INSERT INTO roles (id, tenant_id, key, name) VALUES (@id, @tenant_id, @key, @name)",
                connection,
                transaction);
            insertRole.Parameters.AddWithValue("id", roleId);
            insertRole.Parameters.AddWithValue("tenant_id", tenantId);
            insertRole.Parameters.AddWithValue("key", key);
            insertRole.Parameters.AddWithValue("name", roleName);
            await insertRole.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RoleKeyTakenException();
        }

        await InsertPermissionsAsync(connection, transaction, tenantId, roleId, permissions, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return roleId;
    }

    /// <summary>
    /// Renames a role and replaces its permissions. The requester must hold the role's current permissions
    /// and the new ones.
    /// </summary>
    public async Task UpdateAsync(
        Guid tenantId,
        Guid requesterId,
        Guid roleId,
        string name,
        IEnumerable<string> permissionKeys,
        CancellationToken cancellationToken = default)
    {
        var roleName = CleanName(name);
        var permissions = permissionKeys.Distinct().ToArray();

        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        var current = await RolePermissionsAsync(connection, transaction, roleId, cancellationToken)
            ?? throw new RoleNotFoundException();
        await RequireKnownPermissionsAsync(connection, transaction, permissions, cancellationToken);

        var held = await HeldPermissionsAsync(connection, transaction, tenantId, requesterId, cancellationToken);
        if (!held.IsSupersetOf(current) || !held.IsSupersetOf(permissions))
        {
            throw new RoleExceedsRequesterException();
        }

        await using (var rename = new NpgsqlCommand("UPDATE roles SET name = @name WHERE id = @id", connection, transaction))
        {
            rename.Parameters.AddWithValue("name", roleName);
            rename.Parameters.AddWithValue("id", roleId);
            await rename.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clear = new NpgsqlCommand("DELETE FROM role_permissions WHERE role_id = @id", connection, transaction))
        {
            clear.Parameters.AddWithValue("id", roleId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertPermissionsAsync(connection, transaction, tenantId, roleId, permissions, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes a role, and with it every assignment of that role. The requester must hold the role's permissions.
    /// </summary>
    public async Task DeleteAsync(Guid tenantId, Guid requesterId, Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        var current = await RolePermissionsAsync(connection, transaction, roleId, cancellationToken)
            ?? throw new RoleNotFoundException();

        var held = await HeldPermissionsAsync(connection, transaction, tenantId, requesterId, cancellationToken);
        if (!held.IsSupersetOf(current))
        {
            throw new RoleExceedsRequesterException();
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM roles WHERE id = @id", connection, transaction))
        {
            delete.Parameters.AddWithValue("id", roleId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Gives a role to a person. The requester must hold the role's permissions, and must already hold
    /// every permission the person has, so an admin can't raise a person above their own level.
    /// </summary>
    public async Task AssignAsync(
        Guid tenantId,
        Guid requesterId,
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        var rolePermissions = await RolePermissionsAsync(connection, transaction, roleId, cancellationToken)
            ?? throw new RoleNotFoundException();
        if (!await UserExistsAsync(connection, transaction, userId, cancellationToken))
        {
            throw new UserNotFoundException();
        }

        var held = await HeldPermissionsAsync(connection, transaction, tenantId, requesterId, cancellationToken);
        if (!held.IsSupersetOf(rolePermissions))
        {
            throw new RoleExceedsRequesterException();
        }

        var personPermissions = await UserPermissionsAsync(connection, transaction, tenantId, userId, cancellationToken);
        if (!held.IsSupersetOf(personPermissions))
        {
            throw new AccountRolesExceedRequesterException();
        }

        await using (var assign = new NpgsqlCommand(
            """
            INSERT INTO user_roles (tenant_id, user_id, role_id)
            VALUES (@tenant_id, @user_id, @role_id)
            ON CONFLICT DO NOTHING
            """,
            connection,
            transaction))
        {
            assign.Parameters.AddWithValue("tenant_id", tenantId);
            assign.Parameters.AddWithValue("user_id", userId);
            assign.Parameters.AddWithValue("role_id", roleId);
            await assign.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Takes a role away from a person. The same permission checks as giving it apply.</summary>
    public async Task RemoveAsync(
        Guid tenantId,
        Guid requesterId,
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await openConnection();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        var rolePermissions = await RolePermissionsAsync(connection, transaction, roleId, cancellationToken)
            ?? throw new RoleNotFoundException();
        if (!await UserExistsAsync(connection, transaction, userId, cancellationToken))
        {
            throw new UserNotFoundException();
        }

        var held = await HeldPermissionsAsync(connection, transaction, tenantId, requesterId, cancellationToken);
        if (!held.IsSupersetOf(rolePermissions))
        {
            throw new RoleExceedsRequesterException();
        }

        var personPermissions = await UserPermissionsAsync(connection, transaction, tenantId, userId, cancellationToken);
        if (!held.IsSupersetOf(personPermissions))
        {
            throw new AccountRolesExceedRequesterException();
        }

        await using (var remove = new NpgsqlCommand(
            "DELETE FROM user_roles WHERE user_id = @user_id AND role_id = @role_id",
            connection,
            transaction))
        {
            remove.Parameters.AddWithValue("user_id", userId);
            remove.Parameters.AddWithValue("role_id", roleId);
            await remove.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string CleanName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > 80)
        {
            throw new ArgumentException("A role name is 1 to 80 characters.", nameof(name));
        }

        return trimmed;
    }

    private static async Task RequireKnownPermissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string[] permissions,
        CancellationToken cancellationToken)
    {
        if (permissions.Length == 0)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            "SELECT key FROM permissions WHERE key = ANY(@keys)", connection, transaction);
        command.Parameters.AddWithValue("keys", permissions);
        var known = await ReadKeysAsync(command, cancellationToken);

        foreach (var permission in permissions)
        {
            if (!known.Contains(permission))
            {
                throw new UnknownPermissionException(permission);
            }
        }
    }

    // The permissions the requester holds through roles. An inactive requester holds none.
    private static async Task<HashSet<string>> HeldPermissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid requesterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT rp.permission_key
            FROM user_roles ur
            JOIN role_permissions rp ON rp.role_id = ur.role_id AND rp.tenant_id = ur.tenant_id
            JOIN users u ON u.id = ur.user_id AND u.tenant_id = ur.tenant_id
            WHERE ur.user_id = @user_id AND ur.tenant_id = @tenant_id AND u.status = 'active'
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", requesterId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        return await ReadKeysAsync(command, cancellationToken);
    }

    // Every permission a person has through their roles, whatever their account status.
    private static async Task<HashSet<string>> UserPermissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT rp.permission_key
            FROM user_roles ur
            JOIN role_permissions rp ON rp.role_id = ur.role_id AND rp.tenant_id = ur.tenant_id
            WHERE ur.user_id = @user_id AND ur.tenant_id = @tenant_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        return await ReadKeysAsync(command, cancellationToken);
    }

    // The permissions of a role, or null when the role doesn't exist in this company.
    private static async Task<HashSet<string>?> RolePermissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM roles WHERE id = @id)", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", roleId);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                return null;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT permission_key FROM role_permissions WHERE role_id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", roleId);
        return await ReadKeysAsync(command, cancellationToken);
    }

    private static async Task<bool> UserExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM users WHERE id = @id)", connection, transaction);
        command.Parameters.AddWithValue("id", userId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task InsertPermissionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid roleId,
        string[] permissions,
        CancellationToken cancellationToken)
    {
        foreach (var permission in permissions)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@tenant_id, @role_id, @permission)",
                connection,
                transaction);
            insert.Parameters.AddWithValue("tenant_id", tenantId);
            insert.Parameters.AddWithValue("role_id", roleId);
            insert.Parameters.AddWithValue("permission", permission);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<HashSet<string>> ReadKeysAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }
}
