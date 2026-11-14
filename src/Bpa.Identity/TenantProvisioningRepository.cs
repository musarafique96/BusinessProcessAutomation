using System.Text.RegularExpressions;
using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Identity;

/// <summary>The slug is already used by another company.</summary>
public sealed class TenantSlugTakenException() : Exception("That company code is already taken.");

/// <summary>
/// Creates a company with its first admin. Everything happens in one transaction, so a company never
/// exists without its owner. The owner gets an Owner role that holds every permission in the catalogue.
/// </summary>
public sealed class TenantProvisioningRepository(Func<Task<NpgsqlConnection>> openConnection, PasswordService passwords)
{
    private static readonly Regex SlugFormat = new("^[a-z0-9][a-z0-9-]{2,39}$", RegexOptions.CultureInvariant);

    public async Task<Guid> ProvisionAsync(
        string slug,
        string name,
        string adminEmail,
        string adminFullName,
        string adminPassword,
        CancellationToken cancellationToken = default)
    {
        var normalizedSlug = (slug ?? "").Trim().ToLowerInvariant();
        if (!SlugFormat.IsMatch(normalizedSlug))
        {
            throw new ArgumentException(
                "A company code is 3 to 40 characters: lower case letters, digits and hyphens, not starting with a hyphen.",
                nameof(slug));
        }

        var companyName = (name ?? "").Trim();
        if (companyName.Length is 0 or > 120)
        {
            throw new ArgumentException("A company name is 1 to 120 characters.", nameof(name));
        }

        var fullName = (adminFullName ?? "").Trim();
        if (fullName.Length is 0 or > 120)
        {
            throw new ArgumentException("The admin's name is 1 to 120 characters.", nameof(adminFullName));
        }

        // Hashing first also checks the password length before any database work starts.
        var passwordHash = passwords.Hash(adminPassword);
        var email = UserRepository.NormalizeEmail(adminEmail);

        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var ownerRoleId = Guid.NewGuid();

        await using var connection = await openConnection();

        // TenantDb sets the company id for this transaction, so every insert below is checked by row-level security.
        // The create_tenant call doesn't need the setting, since tenants has no row-level security.
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        try
        {
            await using (var create = new NpgsqlCommand("SELECT create_tenant(@id, @name, @slug)", connection, transaction))
            {
                create.Parameters.AddWithValue("id", tenantId);
                create.Parameters.AddWithValue("name", companyName);
                create.Parameters.AddWithValue("slug", normalizedSlug);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new TenantSlugTakenException();
        }

        await using (var role = new NpgsqlCommand(
            "INSERT INTO roles (id, tenant_id, key, name) VALUES (@id, @tenant_id, 'owner', 'Owner')",
            connection,
            transaction))
        {
            role.Parameters.AddWithValue("id", ownerRoleId);
            role.Parameters.AddWithValue("tenant_id", tenantId);
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        // Copies the whole permission catalogue into the Owner role, so the owner holds every permission.
        await using (var grant = new NpgsqlCommand(
            """
            INSERT INTO role_permissions (tenant_id, role_id, permission_key)
            SELECT @tenant_id, @role_id, key FROM permissions
            """,
            connection,
            transaction))
        {
            grant.Parameters.AddWithValue("tenant_id", tenantId);
            grant.Parameters.AddWithValue("role_id", ownerRoleId);
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var user = new NpgsqlCommand(
            """
            INSERT INTO users (id, tenant_id, email, full_name, password_hash, status)
            VALUES (@id, @tenant_id, @email, @full_name, @password_hash, 'active')
            """,
            connection,
            transaction))
        {
            user.Parameters.AddWithValue("id", ownerId);
            user.Parameters.AddWithValue("tenant_id", tenantId);
            user.Parameters.AddWithValue("email", email);
            user.Parameters.AddWithValue("full_name", fullName);
            user.Parameters.AddWithValue("password_hash", passwordHash);
            await user.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var link = new NpgsqlCommand(
            "INSERT INTO user_roles (tenant_id, user_id, role_id) VALUES (@tenant_id, @user_id, @role_id)",
            connection,
            transaction))
        {
            link.Parameters.AddWithValue("tenant_id", tenantId);
            link.Parameters.AddWithValue("user_id", ownerId);
            link.Parameters.AddWithValue("role_id", ownerRoleId);
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return tenantId;
    }
}
