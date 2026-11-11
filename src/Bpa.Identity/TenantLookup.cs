using Npgsql;

namespace Bpa.Identity;

/// <summary>
/// Finds a company by its public slug, for example the first part of the sign-in request.
/// The tenants table has no row-level security (it is a platform table), so this needs no tenant context.
/// </summary>
public sealed class TenantLookup(Func<Task<NpgsqlConnection>> openConnection)
{
    public async Task<Guid?> FindActiveTenantIdAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        await using var connection = await openConnection();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM tenants WHERE slug = @slug AND status = 'active'",
            connection);
        command.Parameters.AddWithValue("slug", slug.Trim().ToLowerInvariant());

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    /// <summary>The company's public code (its slug), used in emails so people can sign in.</summary>
    public async Task<string?> GetSlugAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = await openConnection();
        await using var command = new NpgsqlCommand("SELECT slug FROM tenants WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", tenantId);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
