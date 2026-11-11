using System.Security.Claims;
using Bpa.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

var app = builder.Build();

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
});

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
