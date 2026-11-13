using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Bpa.Identity;
using Bpa.Identity.Email;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// The application connects as bpa_app, which has no BYPASSRLS, so row-level security applies.
// The password comes from the file named by Db:PasswordFile (a Docker secret), not from the connection string.
var baseConnectionString = builder.Configuration.GetConnectionString("BpaApp")
    ?? throw new InvalidOperationException("ConnectionStrings:BpaApp is not set.");
var dbPasswordFile = builder.Configuration["Db:PasswordFile"];
var connectionString = string.IsNullOrWhiteSpace(dbPasswordFile)
    ? baseConnectionString
    : new NpgsqlConnectionStringBuilder(baseConnectionString)
    {
        Password = File.ReadAllText(dbPasswordFile).Trim()
    }.ConnectionString;
var dataSource = NpgsqlDataSource.Create(connectionString);
var passwords = new PasswordService();

builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton(new UserRepository(async () => await dataSource.OpenConnectionAsync(), passwords));
builder.Services.AddSingleton(new TenantLookup(async () => await dataSource.OpenConnectionAsync()));
builder.Services.AddSingleton(new InvitationRepository(async () => await dataSource.OpenConnectionAsync(), passwords));
builder.Services.AddSingleton(new PasswordResetRepository(async () => await dataSource.OpenConnectionAsync(), passwords));

// The Web client ID from the BPA Google project. It is public, so it is configuration, not a secret.
var googleClientId = builder.Configuration["Google:ClientId"] ?? "";

// Email goes through SMTP when the Smtp section is set. Otherwise nothing is sent, and admins share links.
var smtpOptions = builder.Configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
// The SMTP password comes from the file named by Smtp:PasswordFile (a Docker secret), when set.
var smtpPasswordFile = builder.Configuration["Smtp:PasswordFile"];
if (!string.IsNullOrWhiteSpace(smtpPasswordFile))
{
    smtpOptions.Password = File.ReadAllText(smtpPasswordFile).Trim();
}
builder.Services.AddSingleton<IEmailSender>(smtpOptions.IsConfigured
    ? new SmtpEmailSender(smtpOptions)
    : new NoEmailSender());

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

        // Every request checks the session's security stamp. A password reset changes the stamp,
        // so sessions from before the reset are rejected on their next request.
        options.Events.OnValidatePrincipal = async context =>
        {
            var users = context.HttpContext.RequestServices.GetRequiredService<UserRepository>();
            var principal = context.Principal;
            var validUser = Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId);
            var validTenant = Guid.TryParse(principal?.FindFirstValue("tenant_id"), out var tenantId);
            var validStamp = Guid.TryParse(principal?.FindFirstValue("stamp"), out var stamp);

            if (!validUser || !validTenant || !validStamp
                || await users.GetSecurityStampAsync(tenantId, userId) != stamp)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };

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

    var stamp = await users.GetSecurityStampAsync(tenantId.Value, userId.Value);
    if (stamp is null)
    {
        return Results.Unauthorized();
    }

    // SignInAsync issues a new cookie, so an old session cookie can't be reused after sign-in.
    await SignInCookieAsync(http, userId.Value, tenantId.Value, stamp.Value);
    return Results.NoContent();
}).RequireRateLimiting("sign-in");

// Gmail sign-in: the app sends a Google ID token. We check Google signed it for this app and the email is verified.
// The person must already be invited, so Gmail sign-in never creates an account.
app.MapPost("/auth/google", async (GoogleSignInRequest request, HttpContext http, TenantLookup tenants, UserRepository users) =>
{
    if (string.IsNullOrWhiteSpace(googleClientId))
    {
        return Results.Problem("Gmail sign-in is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(request.Tenant) || string.IsNullOrWhiteSpace(request.IdToken))
    {
        return Results.Unauthorized();
    }

    var tenantId = await tenants.FindActiveTenantIdAsync(request.Tenant);
    if (tenantId is null)
    {
        return Results.Unauthorized();
    }

    GoogleJsonWebSignature.Payload payload;
    try
    {
        payload = await GoogleJsonWebSignature.ValidateAsync(
            request.IdToken,
            new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { googleClientId } });
    }
    catch (InvalidJwtException)
    {
        return Results.Unauthorized();
    }

    if (!payload.EmailVerified || string.IsNullOrEmpty(payload.Email))
    {
        return Results.Unauthorized();
    }

    var userId = await users.SignInWithGoogleAsync(tenantId.Value, payload.Subject, payload.Email);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var stamp = await users.GetSecurityStampAsync(tenantId.Value, userId.Value);
    if (stamp is null)
    {
        return Results.Unauthorized();
    }

    await SignInCookieAsync(http, userId.Value, tenantId.Value, stamp.Value);
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

// Invite a person by email or username, with a role. The token is always returned to the admin.
// When SMTP is configured and an email is given, the invitation is also emailed. Needs users.manage.
app.MapPost("/invitations", async (
    CreateInvitationRequest request,
    ClaimsPrincipal user,
    UserRepository users,
    InvitationRepository invitations,
    TenantLookup tenants,
    IEmailSender mailer,
    ILogger<Program> logger) =>
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

        var emailed = false;
        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            var companyCode = await tenants.GetSlugAsync(tenantId) ?? "";
            emailed = await TrySendAsync(mailer, logger, request.Email, "Your BPA invitation",
                $"You have been invited to BPA.\n\n" +
                $"Company code: {companyCode}\n" +
                $"Invitation code: {created.Token}\n" +
                $"The invitation expires on {created.ExpiresAt:yyyy-MM-dd HH:mm} UTC.");
        }

        return Results.Created($"/invitations/{created.Id}", new
        {
            id = created.Id,
            token = created.Token,
            expiresAt = created.ExpiresAt,
            emailed,
        });
    }
    catch (RoleExceedsInviterException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
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
    InvitationRepository invitations,
    UserRepository users) =>
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

        var stamp = await users.GetSecurityStampAsync(tenantId.Value, userId.Value);
        if (stamp is null)
        {
            return Results.NotFound(new { error = "This invitation is not valid or has expired." });
        }

        await SignInCookieAsync(http, userId.Value, tenantId.Value, stamp.Value);
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

// Create a one-time password reset link for an account. Needs users.manage, and the account can't hold
// permissions the admin lacks. The link is always returned to the admin. When SMTP is configured and the
// account has an email, it is also emailed to the account.
app.MapPost("/users/{id:guid}/password-reset", async (
    Guid id,
    ClaimsPrincipal user,
    UserRepository users,
    PasswordResetRepository resets,
    TenantLookup tenants,
    IEmailSender mailer,
    ILogger<Program> logger) =>
{
    if (!TryGetSession(user, out var tenantId, out var requesterId))
    {
        return Results.Unauthorized();
    }

    if (!await users.HasPermissionAsync(tenantId, requesterId, "users.manage"))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    try
    {
        var created = await resets.CreateAsync(tenantId, requesterId, id);

        var emailed = false;
        if (created.Email is not null)
        {
            var companyCode = await tenants.GetSlugAsync(tenantId) ?? "";
            emailed = await TrySendAsync(mailer, logger, created.Email, "Reset your BPA password",
                $"A password reset was requested for your BPA account.\n\n" +
                $"Company code: {companyCode}\n" +
                $"Reset code: {created.Token}\n" +
                $"The reset code expires on {created.ExpiresAt:yyyy-MM-dd HH:mm} UTC.\n\n" +
                "If you did not ask for this, tell your administrator.");
        }

        return Results.Created($"/users/{id}/password-reset", new
        {
            id = created.Id,
            token = created.Token,
            expiresAt = created.ExpiresAt,
            emailed,
        });
    }
    catch (AccountExceedsRequesterException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (PostgresException ex) when (ex.SqlState == "23503")
    {
        return Results.NotFound(new { error = "Account not found in this company." });
    }
}).RequireAuthorization();

// Set a new password with a reset link. This signs out every existing session for the account.
// The person then signs in as usual.
app.MapPost("/auth/reset-password", async (
    ResetPasswordRequest request,
    TenantLookup tenants,
    PasswordResetRepository resets) =>
{
    var tenantId = await tenants.FindActiveTenantIdAsync(request.Tenant);
    if (tenantId is null)
    {
        return Results.NotFound();
    }

    try
    {
        var userId = await resets.CompleteAsync(tenantId.Value, request.Token, request.Password);
        return userId is null
            ? Results.NotFound(new { error = "This reset link is not valid or has expired." })
            : Results.NoContent();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireRateLimiting("sign-in");

app.Run();

static bool TryGetSession(ClaimsPrincipal user, out Guid tenantId, out Guid userId)
{
    var tenantOk = Guid.TryParse(user.FindFirstValue("tenant_id"), out tenantId);
    var userOk = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    return tenantOk && userOk;
}

static Task SignInCookieAsync(HttpContext http, Guid userId, Guid tenantId, Guid stamp)
{
    var identity = new ClaimsIdentity(
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("stamp", stamp.ToString()),
        },
        CookieAuthenticationDefaults.AuthenticationScheme);

    return http.SignInAsync(new ClaimsPrincipal(identity));
}

// Email is best effort. A failed send is logged, and the admin still has the link in the response.
static async Task<bool> TrySendAsync(IEmailSender mailer, ILogger logger, string to, string subject, string body)
{
    if (!mailer.IsConfigured)
    {
        return false;
    }

    try
    {
        await mailer.SendAsync(to, subject, body);
        return true;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Email to a new account or reset could not be sent.");
        return false;
    }
}

public sealed record SignInRequest(string Tenant, string Username, string Password);

public sealed record GoogleSignInRequest(string? Tenant, string? IdToken);

public sealed record CreateInvitationRequest(string? Email, string? Username, string FullName, Guid RoleId);

public sealed record AcceptInvitationRequest(string Tenant, string Token, string Password);

public sealed record ResetPasswordRequest(string Tenant, string Token, string Password);

// Makes the entry point visible to integration tests added in later phases.
public partial class Program { }
