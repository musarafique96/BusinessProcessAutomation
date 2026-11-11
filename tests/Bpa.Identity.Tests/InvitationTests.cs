using Bpa.Identity;
using Bpa.Tenancy;
using Npgsql;

namespace Bpa.Identity.Tests;

/// <summary>
/// Tests for invitations. They need PostgreSQL with migrations 0001 to 0004 applied, and
/// BPA_TEST_ADMIN_CONN pointing at the owner role.
/// </summary>
public class InvitationTests
{
    private const string Password = "correct horse battery staple";

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("BPA_TEST_ADMIN_CONN")
        ?? throw new InvalidOperationException("Set BPA_TEST_ADMIN_CONN to a test database connection string.");

    private static InvitationRepository CreateInvitations() => new(OpenAppConnectionAsync, new PasswordService());

    private static UserRepository CreateUsers() => new(OpenAppConnectionAsync, new PasswordService());

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
            "INSERT INTO tenants (id, name, slug) VALUES (@id, 'Test company', @slug)", connection);
        command.Parameters.AddWithValue("id", tenantId);
        command.Parameters.AddWithValue("slug", $"co-{tenantId:N}");
        await command.ExecuteNonQueryAsync();
        return tenantId;
    }

    // Creates a role with the given permissions, inside the company's own transaction, as the application would.
    private static async Task<Guid> CreateRoleAsync(Guid tenantId, params string[] permissions)
    {
        var roleId = Guid.NewGuid();
        await using var connection = await OpenAppConnectionAsync();
        await using var transaction = await TenantDb.BeginTenantTransactionAsync(connection, tenantId);

        await using (var command = new NpgsqlCommand(
            "INSERT INTO roles (id, tenant_id, key, name) VALUES (@id, @tenant_id, @key, 'Role')",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("id", roleId);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("key", $"role{roleId:N}");
            await command.ExecuteNonQueryAsync();
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

    // Gives a user a role, as the application would.
    private static async Task AssignRoleAsync(Guid tenantId, Guid userId, Guid roleId)
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

    [Fact]
    public async Task Inviter_cannot_invite_with_a_role_that_has_more_permissions_than_they_hold()
    {
        var tenantId = await CreateTenantAsync();
        var inviterRole = await CreateRoleAsync(tenantId, "users.manage");
        var biggerRole = await CreateRoleAsync(tenantId, "users.manage", "requests.read");
        var inviterId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Inviter", Password);
        await AssignRoleAsync(tenantId, inviterId, inviterRole);

        await Assert.ThrowsAsync<RoleExceedsInviterException>(() =>
            CreateInvitations().CreateAsync(tenantId, inviterId, "Too Much", biggerRole, null, UniqueUsername()));
    }

    [Fact]
    public async Task Inviter_can_invite_with_a_role_they_fully_hold()
    {
        var tenantId = await CreateTenantAsync();
        var inviterRole = await CreateRoleAsync(tenantId, "users.manage", "requests.read");
        var smallerRole = await CreateRoleAsync(tenantId, "requests.read");
        var inviterId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Inviter", Password);
        await AssignRoleAsync(tenantId, inviterId, inviterRole);

        var invitation = await CreateInvitations().CreateAsync(
            tenantId, inviterId, "Fine", smallerRole, null, UniqueUsername());

        Assert.NotEqual(Guid.Empty, invitation.Id);
    }

    private static string UniqueUsername() => $"user{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task Username_invitation_creates_an_account_that_can_sign_in()
    {
        var tenantId = await CreateTenantAsync();
        var roleId = await CreateRoleAsync(tenantId);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", Password);
        var invitations = CreateInvitations();
        var username = UniqueUsername();

        var invitation = await invitations.CreateAsync(tenantId, adminId, "New Person", roleId, null, username);
        var userId = await invitations.AcceptAsync(tenantId, invitation.Token, Password);

        Assert.NotNull(userId);
        Assert.Equal(userId, await CreateUsers().AuthenticateWithUsernameAsync(tenantId, username, Password));
    }

    [Fact]
    public async Task Email_invitation_creates_an_account_that_can_sign_in_with_email()
    {
        var tenantId = await CreateTenantAsync();
        var roleId = await CreateRoleAsync(tenantId);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", Password);
        var invitations = CreateInvitations();
        var email = $"person.{Guid.NewGuid():N}@example.com";

        var invitation = await invitations.CreateAsync(tenantId, adminId, "Email Person", roleId, email, null);
        var userId = await invitations.AcceptAsync(tenantId, invitation.Token, Password);

        Assert.NotNull(userId);
        Assert.Equal(userId, await CreateUsers().AuthenticateAsync(tenantId, email, Password));
    }

    [Fact]
    public async Task Token_works_only_once()
    {
        var tenantId = await CreateTenantAsync();
        var roleId = await CreateRoleAsync(tenantId);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", Password);
        var invitations = CreateInvitations();

        var invitation = await invitations.CreateAsync(tenantId, adminId, "Once", roleId, null, UniqueUsername());
        Assert.NotNull(await invitations.AcceptAsync(tenantId, invitation.Token, Password));

        Assert.Null(await invitations.AcceptAsync(tenantId, invitation.Token, Password));
    }

    [Fact]
    public async Task Expired_invitation_is_rejected()
    {
        var tenantId = await CreateTenantAsync();
        var roleId = await CreateRoleAsync(tenantId);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", Password);
        var invitations = CreateInvitations();

        var invitation = await invitations.CreateAsync(tenantId, adminId, "Late", roleId, null, UniqueUsername());

        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE invitations SET expires_at = now() - interval '1 minute' WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", invitation.Id);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await invitations.AcceptAsync(tenantId, invitation.Token, Password));
    }

    [Fact]
    public async Task Revoked_invitation_is_rejected_and_cannot_be_revoked_twice()
    {
        var tenantId = await CreateTenantAsync();
        var roleId = await CreateRoleAsync(tenantId);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", Password);
        var invitations = CreateInvitations();

        var invitation = await invitations.CreateAsync(tenantId, adminId, "Cancelled", roleId, null, UniqueUsername());

        Assert.True(await invitations.RevokeAsync(tenantId, invitation.Id));
        Assert.False(await invitations.RevokeAsync(tenantId, invitation.Id));
        Assert.Null(await invitations.AcceptAsync(tenantId, invitation.Token, Password));
    }

    [Fact]
    public async Task Token_from_another_company_is_rejected()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();
        var roleId = await CreateRoleAsync(tenantA);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantA, UniqueUsername(), "Admin", Password);
        var invitations = CreateInvitations();

        var invitation = await invitations.CreateAsync(tenantA, adminId, "Company A", roleId, null, UniqueUsername());

        Assert.Null(await invitations.AcceptAsync(tenantB, invitation.Token, Password));
    }

    [Fact]
    public async Task Invitation_needs_exactly_one_of_email_or_username()
    {
        var tenantId = await CreateTenantAsync();
        var roleId = await CreateRoleAsync(tenantId);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", Password);
        var invitations = CreateInvitations();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            invitations.CreateAsync(tenantId, adminId, "Neither", roleId, null, null));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            invitations.CreateAsync(tenantId, adminId, "Both", roleId, "both@example.com", UniqueUsername()));
    }

    [Fact]
    public async Task Short_password_is_rejected_and_the_invitation_stays_usable()
    {
        var tenantId = await CreateTenantAsync();
        var roleId = await CreateRoleAsync(tenantId);
        var adminId = await CreateUsers().CreateUsernameUserAsync(tenantId, UniqueUsername(), "Admin", Password);
        var invitations = CreateInvitations();

        var invitation = await invitations.CreateAsync(tenantId, adminId, "Short", roleId, null, UniqueUsername());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            invitations.AcceptAsync(tenantId, invitation.Token, "short"));
        Assert.NotNull(await invitations.AcceptAsync(tenantId, invitation.Token, Password));
    }
}
