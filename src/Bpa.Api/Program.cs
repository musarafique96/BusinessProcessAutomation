var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Liveness probe used by CI smoke tests and deployment checks.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Makes the entry point visible to integration tests added in later phases.
public partial class Program { }
