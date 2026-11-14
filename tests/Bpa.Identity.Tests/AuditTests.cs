using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Identity.Tests;

/// <summary>
/// Tests for the audit trail. They need PostgreSQL with migrations applied, and BPA_TEST_ADMIN_CONN
/// pointing at the owner role.
/// </summary>
public class AuditTests
{
    private const string Password = "correct horse battery staple";

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("BPA_TEST_ADMIN_CONN")
        ?? throw new InvalidOperationException("Set BPA_TEST_ADMIN_CONN to a test database connection string.");

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

    private static string UniqueEmail() => $"person{Guid.NewGuid():N}@example.com";

    // Counts audit events visible to one company, using the company's own tenant context.
    private static async Task<long> CountAuditEventsAsync(Guid tenantId, string where, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM audit_events WHERE {where}", connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Role_assignment_is_recorded_with_who_did_it()
    {
        var provisioning = new TenantProvisioningRepository(OpenAppConnectionAsync, new PasswordService());
        var users = new UserRepository(OpenAppConnectionAsync, new PasswordService());
        var roles = new RoleRepository(OpenAppConnectionAsync);

        var ownerEmail = UniqueEmail();
        var tenantId = await provisioning.ProvisionAsync(UniqueSlug(), "Audit company", ownerEmail, "Owner", Password);
        var owner = (await users.AuthenticateAsync(tenantId, ownerEmail, Password))!.Value;
        var person = await users.CreateActiveUserAsync(tenantId, UniqueEmail(), "Staff person", Password);
        var roleId = await roles.CreateAsync(tenantId, owner, $"role_{Guid.NewGuid():N}", "Clerk", new[] { "requests.create" });

        await roles.AssignAsync(tenantId, owner, person, roleId);

        var count = await CountAuditEventsAsync(
            tenantId,
            "action = 'role.assigned' AND target_id = @target AND actor_id = @actor",
            ("target", person.ToString()),
            ("actor", owner));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Company_creation_is_recorded()
    {
        var provisioning = new TenantProvisioningRepository(OpenAppConnectionAsync, new PasswordService());
        var tenantId = await provisioning.ProvisionAsync(UniqueSlug(), "Recorded company", UniqueEmail(), "Owner", Password);

        var count = await CountAuditEventsAsync(
            tenantId,
            "action = 'tenant.created' AND target_id = @target",
            ("target", tenantId.ToString()));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task App_role_cannot_change_or_delete_audit_events()
    {
        var provisioning = new TenantProvisioningRepository(OpenAppConnectionAsync, new PasswordService());
        var tenantId = await provisioning.ProvisionAsync(UniqueSlug(), "Locked company", UniqueEmail(), "Owner", Password);

        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId);

        var update = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand("UPDATE audit_events SET action = 'changed'", connection, transaction);
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", update.SqlState);
    }

    [Fact]
    public async Task Company_cannot_read_another_companys_audit_events()
    {
        var provisioning = new TenantProvisioningRepository(OpenAppConnectionAsync, new PasswordService());
        var companyA = await provisioning.ProvisionAsync(UniqueSlug(), "Company A", UniqueEmail(), "Owner A", Password);
        var companyB = await provisioning.ProvisionAsync(UniqueSlug(), "Company B", UniqueEmail(), "Owner B", Password);

        var count = await CountAuditEventsAsync(
            companyB,
            "tenant_id = @tenant",
            ("tenant", companyA));
        Assert.Equal(0, count);
    }
}
