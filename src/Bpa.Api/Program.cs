using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Bpa.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

app.Run();

public sealed record SignInRequest(string Tenant, string Username, string Password);

// Makes the entry point visible to integration tests added in later phases.
public partial class Program { }
