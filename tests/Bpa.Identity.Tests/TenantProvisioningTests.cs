using Npgsql;

namespace Bpa.Identity.Tests;

/// <summary>
/// Tests for creating a company with its first admin. They need PostgreSQL with migrations applied, and
/// BPA_TEST_ADMIN_CONN pointing at the owner role.
/// </summary>
public class TenantProvisioningTests
{
    private const string Password = "correct horse battery staple";

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("BPA_TEST_ADMIN_CONN")
        ?? throw new InvalidOperationException("Set BPA_TEST_ADMIN_CONN to a test database connection string.");

    private static TenantProvisioningRepository CreateProvisioning() =>
        new(OpenAppConnectionAsync, new PasswordService());

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

    private static string UniqueSlug() => $"co-{Guid.NewGuid():N}";

    private static string UniqueEmail() => $"owner{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task New_company_owner_holds_every_permission()
    {
        var provisioning = CreateProvisioning();
        var users = CreateUserRepository();
        var email = UniqueEmail();
        var tenantId = await provisioning.ProvisionAsync(UniqueSlug(), "Test company", email, "Owner Person", Password);

        var ownerId = await users.AuthenticateAsync(tenantId, email, Password);
        Assert.NotNull(ownerId);

        var permissions = await new RoleRepository(OpenAppConnectionAsync).GetPermissionsAsync();
        Assert.NotEmpty(permissions);
        foreach (var permission in permissions)
        {
            Assert.True(
                await users.HasPermissionAsync(tenantId, ownerId.Value, permission.Key),
                $"Owner is missing {permission.Key}.");
        }
    }

    [Fact]
    public async Task Owner_can_sign_in_only_inside_their_own_company()
    {
        var provisioning = CreateProvisioning();
        var users = CreateUserRepository();
        var email = UniqueEmail();
        await provisioning.ProvisionAsync(UniqueSlug(), "Company A", email, "Owner A", Password);
        var otherCompany = await provisioning.ProvisionAsync(UniqueSlug(), "Company B", UniqueEmail(), "Owner B", Password);

        var result = await users.AuthenticateAsync(otherCompany, email, Password);

        Assert.Null(result);
    }

    [Fact]
    public async Task Taken_company_code_is_refused()
    {
        var provisioning = CreateProvisioning();
        var slug = UniqueSlug();
        await provisioning.ProvisionAsync(slug, "First", UniqueEmail(), "First owner", Password);

        await Assert.ThrowsAsync<TenantSlugTakenException>(() =>
            provisioning.ProvisionAsync(slug, "Second", UniqueEmail(), "Second owner", Password));
    }

    [Fact]
    public async Task Short_admin_password_is_refused()
    {
        var provisioning = CreateProvisioning();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provisioning.ProvisionAsync(UniqueSlug(), "Test", UniqueEmail(), "Owner", "short"));
    }

    [Fact]
    public async Task Invalid_company_code_is_refused()
    {
        var provisioning = CreateProvisioning();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provisioning.ProvisionAsync("Not Valid!", "Test", UniqueEmail(), "Owner", Password));
    }

    [Fact]
    public async Task App_role_cannot_insert_companies_directly()
    {
        await using var connection = await OpenAppConnectionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO tenants (id, name, slug) VALUES (@id, 'Sneaky', @slug)", connection);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("slug", UniqueSlug());
            await command.ExecuteNonQueryAsync();
        });

        Assert.Equal("42501", error.SqlState);
    }
}
