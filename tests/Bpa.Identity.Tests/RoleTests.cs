using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Identity.Tests;

/// <summary>
/// Tests for roles and the permission cap. They need PostgreSQL with migrations applied, and BPA_TEST_ADMIN_CONN
/// pointing at the owner role.
/// </summary>
public class RoleTests
{
    private const string Password = "correct horse battery staple";

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("BPA_TEST_ADMIN_CONN")
        ?? throw new InvalidOperationException("Set BPA_TEST_ADMIN_CONN to a test database connection string.");

    private static RoleRepository CreateRepository() => new(OpenAppConnectionAsync);

    private static UserRepository CreateUserRepository() => new(OpenAppConnectionAsync, new PasswordService());

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

    // Creates a company with a unique slug. Runs as the owner, since the tenants table is a platform table.
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

    private static string UniqueKey() => $"role_{Guid.NewGuid():N}";

    private static string UniqueEmail() => $"user{Guid.NewGuid():N}@example.com";

    // Creates a role that holds the given permissions. Written as the app role, inside the company.
    private static async Task<Guid> CreateRoleWithAsync(Guid tenantId, params string[] permissions)
    {
        var roleId = Guid.NewGuid();
        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO roles (id, tenant_id, key, name) VALUES (@id, @tenant_id, @key, 'Test role')",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("id", roleId);
            insert.Parameters.AddWithValue("tenant_id", tenantId);
            insert.Parameters.AddWithValue("key", UniqueKey());
            await insert.ExecuteNonQueryAsync();
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

    // Gives a role to a person, inside the company.
    private static async Task GrantRoleAsync(Guid tenantId, Guid userId, Guid roleId)
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

    // A person with no roles.
    private static async Task<Guid> CreatePersonAsync(Guid tenantId) =>
        await CreateUserRepository().CreateActiveUserAsync(tenantId, UniqueEmail(), "Test person", Password);

    // A person who holds the given permissions through one role.
    private static async Task<Guid> CreatePersonWithAsync(Guid tenantId, params string[] permissions)
    {
        var userId = await CreatePersonAsync(tenantId);
        var roleId = await CreateRoleWithAsync(tenantId, permissions);
        await GrantRoleAsync(tenantId, userId, roleId);
        return userId;
    }

    [Fact]
    public async Task Person_can_create_a_role_with_permissions_they_hold()
    {
        var tenantId = await CreateTenantAsync();
        var manager = await CreatePersonWithAsync(tenantId, "roles.manage", "requests.create");
        var repository = CreateRepository();
        var key = UniqueKey();

        var roleId = await repository.CreateAsync(tenantId, manager, key, "Clerk", new[] { "requests.create" });

        var created = Assert.Single((await repository.ListRolesAsync(tenantId)).Where(role => role.Id == roleId));
        Assert.Equal(key, created.Key);
        Assert.Equal(new[] { "requests.create" }, created.Permissions);
    }

    [Fact]
    public async Task Person_cannot_create_a_role_with_a_permission_they_do_not_hold()
    {
        var tenantId = await CreateTenantAsync();
        var manager = await CreatePersonWithAsync(tenantId, "roles.manage");
        var repository = CreateRepository();

        await Assert.ThrowsAsync<RoleExceedsRequesterException>(() =>
            repository.CreateAsync(tenantId, manager, UniqueKey(), "Clerk", new[] { "requests.create" }));
    }

    [Fact]
    public async Task Person_cannot_edit_a_role_that_holds_a_permission_they_do_not_have()
    {
        var tenantId = await CreateTenantAsync();
        var manager = await CreatePersonWithAsync(tenantId, "roles.manage");
        var roleId = await CreateRoleWithAsync(tenantId, "requests.create");
        var repository = CreateRepository();

        await Assert.ThrowsAsync<RoleExceedsRequesterException>(() =>
            repository.UpdateAsync(tenantId, manager, roleId, "Renamed", new[] { "requests.create" }));
    }

    [Fact]
    public async Task Person_cannot_give_a_role_with_permissions_they_do_not_hold()
    {
        var tenantId = await CreateTenantAsync();
        var manager = await CreatePersonWithAsync(tenantId, "users.manage");
        var target = await CreatePersonAsync(tenantId);
        var roleId = await CreateRoleWithAsync(tenantId, "requests.create");
        var repository = CreateRepository();

        await Assert.ThrowsAsync<RoleExceedsRequesterException>(() =>
            repository.AssignAsync(tenantId, manager, target, roleId));
    }

    [Fact]
    public async Task Person_cannot_change_roles_of_someone_who_holds_more_than_they_do()
    {
        var tenantId = await CreateTenantAsync();
        var manager = await CreatePersonWithAsync(tenantId, "users.manage");
        var owner = await CreatePersonWithAsync(tenantId, "users.manage", "roles.manage", "requests.create");
        var harmlessRoleId = await CreateRoleWithAsync(tenantId);
        var repository = CreateRepository();

        await Assert.ThrowsAsync<AccountRolesExceedRequesterException>(() =>
            repository.AssignAsync(tenantId, manager, owner, harmlessRoleId));
    }

    [Fact]
    public async Task Roles_of_one_company_are_not_visible_to_another()
    {
        var companyA = await CreateTenantAsync();
        var companyB = await CreateTenantAsync();
        var roleId = await CreateRoleWithAsync(companyA, "requests.create");
        var repository = CreateRepository();

        var rolesOfB = await repository.ListRolesAsync(companyB);

        Assert.DoesNotContain(rolesOfB, role => role.Id == roleId);
    }
}
