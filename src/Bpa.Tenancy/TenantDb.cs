using Npgsql;

namespace Bpa.Tenancy;

/// <summary>
/// Starts database work for exactly one tenant.
///
/// The tenant id is set with <c>set_config(..., is_local = true)</c>, so it lasts only until the
/// transaction ends. A pooled connection therefore never carries one tenant's id into another
/// request. Row-level security policies read this setting and return no rows when it is absent.
/// </summary>
public static class TenantDb
{
    public const string SettingName = "app.tenant_id";

    public static async Task<NpgsqlTransaction> BeginTenantTransactionAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var command = new NpgsqlCommand("select set_config(@name, @value, true)", connection, transaction);
        command.Parameters.AddWithValue("name", SettingName);
        command.Parameters.AddWithValue("value", tenantId.ToString());
        await command.ExecuteScalarAsync(cancellationToken);

        return transaction;
    }
}
