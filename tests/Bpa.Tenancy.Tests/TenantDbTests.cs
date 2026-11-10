using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Tenancy.Tests;

/// <summary>
/// Tests for the tenant-scoped transaction helper. They need a PostgreSQL database with migrations
/// applied (see README). The connection string comes from BPA_TEST_ADMIN_CONN.
/// Row-level security itself is covered by tests/sql/rls_checks.sql, which CI also runs.
/// </summary>
public class TenantDbTests
{
    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("BPA_TEST_ADMIN_CONN")
        ?? throw new InvalidOperationException("Set BPA_TEST_ADMIN_CONN to a test database connection string.");

    [Fact]
    public async Task Tenant_setting_is_visible_inside_the_transaction()
    {
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await using var connection = new NpgsqlConnection(AdminConnectionString);

        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId);
        await using var command = new NpgsqlCommand(
            $"select current_setting('{TenantDb.SettingName}', true)", connection, transaction);
        var value = (string?)await command.ExecuteScalarAsync();

        Assert.Equal(tenantId.ToString(), value);
    }

    [Fact]
    public async Task Tenant_setting_does_not_survive_the_transaction()
    {
        var tenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        await using var connection = new NpgsqlConnection(AdminConnectionString);

        await using (var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId))
        {
            await transaction.CommitAsync();
        }

        await using var command = new NpgsqlCommand(
            $"select coalesce(current_setting('{TenantDb.SettingName}', true), '')", connection);
        var value = (string?)await command.ExecuteScalarAsync();

        Assert.Equal(string.Empty, value);
    }
}
