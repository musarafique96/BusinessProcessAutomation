using Bpa.Identity;
using Npgsql;

namespace Bpa.Identity.Tests;

/// <summary>
/// Tests for Gmail sign-in. They need PostgreSQL with migrations applied, and BPA_TEST_ADMIN_CONN
/// pointing at the owner role.
/// </summary>
public class GoogleSignInTests
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

    private static string UniqueEmail() => $"gmail{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Invited_account_is_linked_on_first_verified_sign_in_and_matched_by_subject_after()
    {
        var tenantId = await CreateTenantAsync();
        var repository = CreateRepository();
        var email = UniqueEmail();
        var userId = await repository.CreateActiveUserAsync(tenantId, email, "Ayesha Khan", Password);
        var subject = Guid.NewGuid().ToString("N");

        Assert.Equal(userId, await repository.SignInWithGoogleAsync(tenantId, subject, email));
        Assert.Equal(userId, await repository.SignInWithGoogleAsync(tenantId, subject, email));
    }

    [Fact]
    public async Task Person_who_was_not_invited_cannot_sign_in_with_Gmail()
    {
        var tenantId = await CreateTenantAsync();
        var repository = CreateRepository();

        var result = await repository.SignInWithGoogleAsync(tenantId, Guid.NewGuid().ToString("N"), UniqueEmail());

        Assert.Null(result);
    }

    [Fact]
    public async Task Account_linked_to_another_Google_login_is_refused()
    {
        var tenantId = await CreateTenantAsync();
        var repository = CreateRepository();
        var email = UniqueEmail();
        await repository.CreateActiveUserAsync(tenantId, email, "Ayesha Khan", Password);
        await repository.SignInWithGoogleAsync(tenantId, Guid.NewGuid().ToString("N"), email);

        var otherLogin = await repository.SignInWithGoogleAsync(tenantId, Guid.NewGuid().ToString("N"), email);

        Assert.Null(otherLogin);
    }
}
