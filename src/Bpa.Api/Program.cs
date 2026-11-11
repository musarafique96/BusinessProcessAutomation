using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Bpa.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// The application connects as bpa_app, which has no BYPASSRLS, so row-level security applies.
var connectionString = builder.Configuration.GetConnectionString("BpaApp")
    ?? throw new InvalidOperationException("ConnectionStrings:BpaApp is not set.");
var dataSource = NpgsqlDataSource.Create(connectionString);
var passwords = new PasswordService();

builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(new UserRepository(async () => await dataSource.OpenConnectionAsync(), passwords));
builder.Services.AddSingleton(new TenantLookup(async () => await dataSource.OpenConnectionAsync()));
builder.Services.AddSingleton(new InvitationRepository(async () => await dataSource.OpenConnectionAsync(), passwords));

// Cookie sessions. The cookie is HttpOnly and sent only over HTTPS.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "bpa.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        // API clients get 401 and 403, not a redirect to a login page.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

// Session keys are saved to disk so a restart doesn't sign everyone out.
// In deployment, point this at a folder on the Docker volume.
var keysPath = builder.Configuration["DataProtection:KeysPath"] ?? Path.Combine(AppContext.BaseDirectory, "dpkeys");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Caddy sits in front of the API. Client addresses come from X-Forwarded-For only when the
// request arrives from a trusted proxy (set TrustedProxies in configuration to Caddy's address).
var trustedProxies = builder.Configuration.GetSection("TrustedProxies").Get<string[]>() ?? ["127.0.0.1", "::1"];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Clear();
    foreach (var proxy in trustedProxies)
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

// Sign-in attempts are limited per client address: 10 attempts per 15 minutes.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("sign-in", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Liveness probe used by CI smoke tests and deployment checks.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Sign in with company slug, username and password. Unknown company, unknown user and wrong password
// all return the same 401, so the response doesn't reveal which part was wrong.
app.MapPost("/auth/sign-in", async (SignInRequest request, HttpContext http, TenantLookup tenants, UserRepository users) =>
{
    var tenantId = await tenants.FindActiveTenantIdAsync(request.Tenant);
    if (tenantId is null)
    {
        return Results.Unauthorized();
    }

    var userId = await users.AuthenticateWithUsernameAsync(tenantId.Value, request.Username, request.Password);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var identity = new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
            new Claim("tenant_id", tenantId.Value.ToString()),
        },
        CookieAuthenticationDefaults.AuthenticationScheme);

    // SignInAsync issues a new cookie, so an old session cookie can't be reused after sign-in.
    await http.SignInAsync(new ClaimsPrincipal(identity));
    return Results.NoContent();
}).RequireRateLimiting("sign-in");

app.MapPost("/auth/sign-out", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/auth/me", (ClaimsPrincipal user) => Results.Ok(new
{
    userId = user.FindFirstValue(ClaimTypes.NameIdentifier),
    tenantId = user.FindFirstValue("tenant_id"),
})).RequireAuthorization();

// Invite a person by email or username, with a role. The token is returned once. Email delivery
// isn't set up yet, so the admin shares the link. Needs users.manage.
app.MapPost("/invitations", async (
    CreateInvitationRequest request,
    ClaimsPrincipal user,
    UserRepository users,
    InvitationRepository invitations) =>
{
    if (!TryGetSession(user, out var tenantId, out var userId))
    {
        return Results.Unauthorized();
    }

    if (!await users.HasPermissionAsync(tenantId, userId, "users.manage"))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    try
    {
        var created = await invitations.CreateAsync(
            tenantId, userId, request.FullName, request.RoleId, request.Email, request.Username);
        return Results.Created($"/invitations/{created.Id}", new
        {
            id = created.Id,
            token = created.Token,
            expiresAt = created.ExpiresAt,
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (PostgresException ex) when (ex.SqlState == "23503")
    {
        return Results.BadRequest(new { error = "Role not found in this company." });
    }
    catch (PostgresException ex) when (ex.SqlState == "23505")
    {
        return Results.Conflict(new { error = "A pending invitation already exists for this email or username." });
    }
}).RequireAuthorization();

app.MapPost("/invitations/{id:guid}/revoke", async (
    Guid id,
    ClaimsPrincipal user,
    UserRepository users,
    InvitationRepository invitations) =>
{
    if (!TryGetSession(user, out var tenantId, out var userId))
    {
        return Results.Unauthorized();
    }

    if (!await users.HasPermissionAsync(tenantId, userId, "users.manage"))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return await invitations.RevokeAsync(tenantId, id)
        ? Results.NoContent()
        : Results.NotFound();
}).RequireAuthorization();

// Accept an invitation: set a password, which creates the account, then sign in.
// Unknown company, unknown or used token all return 404. The token is 256 bits, so guessing is not practical.
app.MapPost("/auth/accept-invitation", async (
    AcceptInvitationRequest request,
    HttpContext http,
    TenantLookup tenants,
    InvitationRepository invitations) =>
{
    var tenantId = await tenants.FindActiveTenantIdAsync(request.Tenant);
    if (tenantId is null)
    {
        return Results.NotFound();
    }

    try
    {
        var userId = await invitations.AcceptAsync(tenantId.Value, request.Token, request.Password);
        if (userId is null)
        {
            return Results.NotFound(new { error = "This invitation is not valid or has expired." });
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
                new Claim("tenant_id", tenantId.Value.ToString()),
            },
            CookieAuthenticationDefaults.AuthenticationScheme);

        await http.SignInAsync(new ClaimsPrincipal(identity));
        return Results.NoContent();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (PostgresException ex) when (ex.SqlState == "23505")
    {
        return Results.Conflict(new { error = "An account already exists for this email or username." });
    }
}).RequireRateLimiting("sign-in");

app.Run();

static bool TryGetSession(ClaimsPrincipal user, out Guid tenantId, out Guid userId)
{
    var tenantOk = Guid.TryParse(user.FindFirstValue("tenant_id"), out tenantId);
    var userOk = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    return tenantOk && userOk;
}

public sealed record SignInRequest(string Tenant, string Username, string Password);

public sealed record CreateInvitationRequest(string? Email, string? Username, string FullName, Guid RoleId);

public sealed record AcceptInvitationRequest(string Tenant, string Token, string Password);

// Makes the entry point visible to integration tests added in later phases.
public partial class Program { }
