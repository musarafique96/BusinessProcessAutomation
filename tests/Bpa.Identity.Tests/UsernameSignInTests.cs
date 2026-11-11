using Bpa.Identity;
using Npgsql;

namespace Bpa.Identity.Tests;

/// <summary>
/// Tests for username sign-in and company lookup. They need PostgreSQL with migrations 0001 to 0003
/// applied, and BPA_TEST_ADMIN_CONN pointing at the owner role.
/// </summary>
public class UsernameSignInTests
{
    private const string Password = "correct horse battery staple";

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("BPA_TEST_ADMIN_CONN")
        ?? throw new InvalidOperationException("Set BPA_TEST_ADMIN_CONN to a test database connection string.");

    private static UserRepository CreateRepository() => new(OpenAppConnectionAsync, new PasswordService());

    private static TenantLookup CreateTenantLookup() => new(OpenAppConnectionAsync);

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
    private static async Task<(Guid Id, string Slug)> CreateTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var slug = $"co-{tenantId:N}";
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "INSERT INTO tenants (id, name, slug) VALUES (@id, 'Test company', @slug)", connection);
        command.Parameters.AddWithValue("id", tenantId);
        command.Parameters.AddWithValue("slug", slug);
        await command.ExecuteNonQueryAsync();
        return (tenantId, slug);
    }

    private static string UniqueUsername() => $"user{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task User_signs_in_with_username_and_password()
    {
        var (tenantId, _) = await CreateTenantAsync();
        var repository = CreateRepository();
        var username = UniqueUsername();
        var userId = await repository.CreateUsernameUserAsync(tenantId, username, "Ayesha Khan", Password);

        var signedIn = await repository.AuthenticateWithUsernameAsync(tenantId, username, Password);

        Assert.Equal(userId, signedIn);
    }

    [Fact]
    public async Task Username_is_case_insensitive_at_sign_in()
    {
        var (tenantId, _) = await CreateTenantAsync();
        var repository = CreateRepository();
        var username = UniqueUsername();
        var userId = await repository.CreateUsernameUserAsync(tenantId, username, "Case Test", Password);

        var signedIn = await repository.AuthenticateWithUsernameAsync(tenantId, username.ToUpperInvariant(), Password);

        Assert.Equal(userId, signedIn);
    }

    [Fact]
    public async Task Wrong_password_for_a_username_is_rejected()
    {
        var (tenantId, _) = await CreateTenantAsync();
        var repository = CreateRepository();
        var username = UniqueUsername();
        await repository.CreateUsernameUserAsync(tenantId, username, "Wrong Password", Password);

        var signedIn = await repository.AuthenticateWithUsernameAsync(tenantId, username, "not the password at all");

        Assert.Null(signedIn);
    }

    [Fact]
    public async Task Username_from_another_company_cannot_sign_in()
    {
        var (tenantA, _) = await CreateTenantAsync();
        var (tenantB, _) = await CreateTenantAsync();
        var repository = CreateRepository();
        var username = UniqueUsername();
        await repository.CreateUsernameUserAsync(tenantA, username, "Company A", Password);

        var signedIn = await repository.AuthenticateWithUsernameAsync(tenantB, username, Password);

        Assert.Null(signedIn);
    }

    [Fact]
    public async Task Duplicate_username_in_one_company_is_rejected()
    {
        var (tenantId, _) = await CreateTenantAsync();
        var repository = CreateRepository();
        var username = UniqueUsername();
        await repository.CreateUsernameUserAsync(tenantId, username, "First", Password);

        await Assert.ThrowsAsync<PostgresException>(() =>
            repository.CreateUsernameUserAsync(tenantId, username, "Second", Password));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("-starts.with.dash")]
    [InlineData("this.username.is.far.too.long.to.be.allowed")]
    public async Task Invalid_username_is_rejected_before_the_database(string username)
    {
        var (tenantId, _) = await CreateTenantAsync();
        var repository = CreateRepository();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.CreateUsernameUserAsync(tenantId, username, "Bad Name", Password));
    }

    [Fact]
    public async Task Company_lookup_finds_an_active_slug_and_ignores_unknown_ones()
    {
        var (tenantId, slug) = await CreateTenantAsync();
        var lookup = CreateTenantLookup();

        Assert.Equal(tenantId, await lookup.FindActiveTenantIdAsync(slug));
        Assert.Null(await lookup.FindActiveTenantIdAsync($"missing-{Guid.NewGuid():N}"));
    }
}
