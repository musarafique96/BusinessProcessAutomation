using Bpa.Identity;
using Npgsql;

namespace Bpa.Identity.Tests;

/// <summary>
/// Tests for user accounts and permission checks. They need PostgreSQL with migrations 0001 and 0002
/// applied. BPA_TEST_ADMIN_CONN points at the owner role. The tests act as bpa_app to exercise RLS.
/// </summary>
public class UserRepositoryTests
{
    private const string Password = "correct horse battery staple";

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("BPA_TEST_ADMIN_CONN")
        ?? throw new InvalidOperationException("Set BPA_TEST_ADMIN_CONN to a test database connection string.");

    private static UserRepository CreateRepository() => new(OpenAppConnectionAsync, new PasswordService());

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
            "INSERT INTO tenants (id, name, slug) VALUES (@id, 'Test tenant', @slug)", connection);
        command.Parameters.AddWithValue("id", tenantId);
        command.Parameters.AddWithValue("slug", tenantId.ToString("N"));
        await command.ExecuteNonQueryAsync();
        return tenantId;
    }

    private static string UniqueEmail() => $"user-{Guid.NewGuid():N}@test.example";

    // Creates a role with one permission and assigns it to the user. Runs as the owner (bypasses RLS).
    private static async Task GrantPermissionAsync(Guid tenantId, Guid userId, string permissionKey)
    {
        var roleId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        await using (var role = new NpgsqlCommand(
            "INSERT INTO roles (id, tenant_id, key, name) VALUES (@id, @tenant, @key, 'Test role')", connection))
        {
            role.Parameters.AddWithValue("id", roleId);
            role.Parameters.AddWithValue("tenant", tenantId);
            role.Parameters.AddWithValue("key", $"role-{roleId:N}");
            await role.ExecuteNonQueryAsync();
        }

        await using (var permission = new NpgsqlCommand(
            "INSERT INTO role_permissions (tenant_id, role_id, permission_key) VALUES (@tenant, @role, @permission)", connection))
        {
            permission.Parameters.AddWithValue("tenant", tenantId);
            permission.Parameters.AddWithValue("role", roleId);
            permission.Parameters.AddWithValue("permission", permissionKey);
            await permission.ExecuteNonQueryAsync();
        }

        await using var assignment = new NpgsqlCommand(
            "INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@tenant, @user, @role)", connection);
        assignment.Parameters.AddWithValue("tenant", tenantId);
        assignment.Parameters.AddWithValue("user", userId);
        assignment.Parameters.AddWithValue("role", roleId);
        await assignment.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task User_signs_in_with_the_correct_password()
    {
        var tenantId = await CreateTenantAsync();
        var repository = CreateRepository();
        var email = UniqueEmail();

        var userId = await repository.CreateActiveUserAsync(tenantId, email, "Ayesha Khan", Password);
        var signedIn = await repository.AuthenticateAsync(tenantId, email, Password);

        Assert.Equal(userId, signedIn);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var tenantId = await CreateTenantAsync();
        var repository = CreateRepository();
        var email = UniqueEmail();
        await repository.CreateActiveUserAsync(tenantId, email, "Bilal Raza", Password);

        var signedIn = await repository.AuthenticateAsync(tenantId, email, "wrong password entirely");

        Assert.Null(signedIn);
    }

    [Fact]
    public async Task Same_email_is_allowed_in_two_tenants()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var repository = CreateRepository();
        var email = UniqueEmail();

        var userA = await repository.CreateActiveUserAsync(tenantA, email, "A User", Password);
        var userB = await repository.CreateActiveUserAsync(tenantB, email, "B User", Password);

        Assert.NotEqual(userA, userB);
    }

    [Fact]
    public async Task Duplicate_email_in_one_tenant_is_rejected()
    {
        var tenantId = await CreateTenantAsync();
        var repository = CreateRepository();
        var email = UniqueEmail();
        await repository.CreateActiveUserAsync(tenantId, email, "First", Password);

        await Assert.ThrowsAsync<PostgresException>(() =>
            repository.CreateActiveUserAsync(tenantId, email, "Second", Password));
    }

    [Fact]
    public async Task User_from_another_tenant_cannot_sign_in()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var repository = CreateRepository();
        var email = UniqueEmail();
        await repository.CreateActiveUserAsync(tenantA, email, "A User", Password);

        var signedIn = await repository.AuthenticateAsync(tenantB, email, Password);

        Assert.Null(signedIn);
    }

    [Fact]
    public async Task Permission_comes_only_from_an_assigned_role()
    {
        var tenantId = await CreateTenantAsync();
        var repository = CreateRepository();
        var userId = await repository.CreateActiveUserAsync(tenantId, UniqueEmail(), "Role Holder", Password);

        Assert.False(await repository.HasPermissionAsync(tenantId, userId, "users.manage"));

        await GrantPermissionAsync(tenantId, userId, "users.manage");

        Assert.True(await repository.HasPermissionAsync(tenantId, userId, "users.manage"));
        Assert.False(await repository.HasPermissionAsync(tenantId, userId, "roles.manage"));
    }

    [Fact]
    public async Task Password_shorter_than_the_minimum_is_rejected()
    {
        var tenantId = await CreateTenantAsync();
        var repository = CreateRepository();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.CreateActiveUserAsync(tenantId, UniqueEmail(), "Short", "too short"));
    }
}
