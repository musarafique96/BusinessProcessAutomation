using Bpa.Identity;
using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Identity.Tests;

/// <summary>
/// Tests for admin-issued password resets. They need PostgreSQL with migrations 0001 to 0005 applied,
/// and BPA_TEST_ADMIN_CONN pointing at the owner role.
/// </summary>
public class PasswordResetTests
{
    private const string OldPassword = "correct horse battery staple";
    private const string NewPassword = "a brand new passphrase";

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("BPA_TEST_ADMIN_CONN")
        ?? throw new InvalidOperationException("Set BPA_TEST_ADMIN_CONN to a test database connection string.");

    private static PasswordResetRepository CreateResets() => new(OpenAppConnectionAsync, new PasswordService());

    private static UserRepository CreateUsers() => new(OpenAppConnectionAsync, new PasswordService());

    // Pooling is off so the SET ROLE can't leak into other tests that share a pooled connection.
    private static async Task<NpgsqlConnection> OpenAppConnectionAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SET ROLE bpa_app", connection);
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<Guid> CreateTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "INSERT INTO tenants (id, name, slug) VALUES (@id, 'Test company', @slug)", connection);
        command.Parameters.AddWithValue("id", tenantId);
        command.Parameters.AddWithValue("slug", $"co-{tenantId:N}");
        await command.ExecuteNonQueryAsync();
        return tenantId;
    }

    private static async Task<Guid> CreateRoleAsync(Guid tenantId, params string[] permissions)
    {
        var roleId = Guid.NewGuid();
        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId);

        await using (var command = new NpgsqlCommand(
            "INSERT INTO roles (id, tenant_id, key, name) VALUES (@id, @tenant_id, @key, 'Role')",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", roleId);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("key", $"role{roleId:N}");
            await command.ExecuteNonQueryAsync();
        }

        foreach (var permission in permissions)
        {
            await using var grant = new NpgsqlCommand(
                "INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@tenant_id, @role_id, @permission)",
                connection,
                transaction);
            grant.Parameters.AddWithValue("tenant_id", tenantId);
            grant.Parameters.AddWithValue("role_id", roleId);
            grant.Parameters.AddWithValue("permission", permission);
            await grant.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return roleId;
    }

    private static async Task AssignRoleAsync(Guid tenantId, Guid userId, Guid roleId)
    {
        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId);
        await using var command = new NpgsqlCommand(
            "INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@tenant_id, @user_id, @role_id)",
            connection,
            transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("role_id", roleId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static string UniqueUsername() => $"user{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task Reset_link_sets_a_new_password_and_the_old_one_stops_working()
    {
        var tenantId = await CreateTenantAsync();
        var username = UniqueUsername();
        var userId = await CreateUsers().CreateUsernameUserAsync(tenantId, username, "Staff", OldPassword);
        var adminRole = await CreateRoleAsync(tenantId, "users.manage");
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", OldPassword);
        await AssignRoleAsync(tenantId, adminId, adminRole);
        var resets = CreateResets();

        var reset = await resets.CreateAsync(tenantId, adminId, userId);
        Assert.Equal(userId, await resets.CompleteAsync(tenantId, reset.Token, NewPassword));

        var users = CreateUsers();
        Assert.Equal(userId, await users.AuthenticateWithUsernameAsync(tenantId, username, NewPassword));
        Assert.Null(await users.AuthenticateWithUsernameAsync(tenantId, username, OldPassword));
    }

    [Fact]
    public async Task Reset_link_works_only_once()
    {
        var tenantId = await CreateTenantAsync();
        var userId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Staff", OldPassword);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", OldPassword);
        var resets = CreateResets();

        var reset = await resets.CreateAsync(tenantId, adminId, userId);
        Assert.NotNull(await resets.CompleteAsync(tenantId, reset.Token, NewPassword));

        Assert.Null(await resets.CompleteAsync(tenantId, reset.Token, "yet another passphrase"));
    }

    [Fact]
    public async Task A_new_reset_replaces_the_earlier_link()
    {
        var tenantId = await CreateTenantAsync();
        var userId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Staff", OldPassword);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", OldPassword);
        var resets = CreateResets();

        var first = await resets.CreateAsync(tenantId, adminId, userId);
        var second = await resets.CreateAsync(tenantId, adminId, userId);

        Assert.Null(await resets.CompleteAsync(tenantId, first.Token, NewPassword));
        Assert.Equal(userId, await resets.CompleteAsync(tenantId, second.Token, NewPassword));
    }

    [Fact]
    public async Task Expired_reset_link_is_rejected()
    {
        var tenantId = await CreateTenantAsync();
        var userId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Staff", OldPassword);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", OldPassword);
        var resets = CreateResets();

        var reset = await resets.CreateAsync(tenantId, adminId, userId);

        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE password_resets SET expires_at = now() - interval '1 minute' WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", reset.Id);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await resets.CompleteAsync(tenantId, reset.Token, NewPassword));
    }

    [Fact]
    public async Task Admin_cannot_reset_an_account_that_holds_permissions_the_admin_lacks()
    {
        var tenantId = await CreateTenantAsync();
        var adminRole = await CreateRoleAsync(tenantId, "users.manage");
        var higherRole = await CreateRoleAsync(tenantId, "requests.read");
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", OldPassword);
        await AssignRoleAsync(tenantId, adminId, adminRole);
        var targetId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Target", OldPassword);
        await AssignRoleAsync(tenantId, targetId, higherRole);

        await Assert.ThrowsAsync<AccountExceedsRequesterException>(() =>
            CreateResets().CreateAsync(tenantId, adminId, targetId));
    }

    [Fact]
    public async Task Short_new_password_is_rejected_and_the_link_stays_usable()
    {
        var tenantId = await CreateTenantAsync();
        var userId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Staff", OldPassword);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", OldPassword);
        var resets = CreateResets();

        var reset = await resets.CreateAsync(tenantId, adminId, userId);

        await Assert.ThrowsAsync<ArgumentException>(() => resets.CompleteAsync(tenantId, reset.Token, "short"));
        Assert.Equal(userId, await resets.CompleteAsync(tenantId, reset.Token, NewPassword));
    }

    [Fact]
    public async Task Reset_changes_the_security_stamp_so_existing_sessions_stop()
    {
        var tenantId = await CreateTenantAsync();
        var userId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Staff", OldPassword);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", OldPassword);
        var users = CreateUsers();
        var resets = CreateResets();

        var before = await users.GetSecurityStampAsync(tenantId, userId);
        var reset = await resets.CreateAsync(tenantId, adminId, userId);
        await resets.CompleteAsync(tenantId, reset.Token, NewPassword);
        var after = await users.GetSecurityStampAsync(tenantId, userId);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }
}
