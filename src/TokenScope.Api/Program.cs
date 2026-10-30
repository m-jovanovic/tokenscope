using ModelContextProtocol.AspNetCore;
using Npgsql;
using TokenScope.Api.Features.Analytics;
using TokenScope.Api.Features.Analytics.Mcp;
using TokenScope.Api.Features.Demo;
using TokenScope.Api.Features.Diagnostics;
using TokenScope.Api.Features.History;
using TokenScope.Api.Features.LocalUsage;
using TokenScope.Api.Features.Telemetry;
using TokenScope.Api.Infrastructure.Database;
using TokenScope.Api.Infrastructure.Http;

var builder = WebApplication.CreateBuilder(args);

string connectionString;
try
{
    connectionString = DatabaseConfiguration.ConnectionString(
        builder.Configuration.GetConnectionString("tokenscope"),
        builder.Configuration.GetConnectionString("Database"));
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}

builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddSingleton<UsageEventWriter>();
builder.Services.AddRequestDecompression();
builder.Services.AddTelemetry();
builder.Services.AddHistory();
builder.Services.AddLocalUsage(builder.Configuration);
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<SpendTools>();

await using var app = builder.Build();

try
{
    await DatabaseSchema.EnsureCreatedAsync(
        app.Services.GetRequiredService<NpgsqlDataSource>(),
        app.Logger,
        app.Lifetime.ApplicationStopping);
}
catch (InvalidOperationException exception)
{
    app.Logger.LogError("Startup failed: {Reason}", exception.Message);
    Environment.ExitCode = 1;
    return;
}

app.UseRequestDecompression();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<RequestBoundaryMiddleware>();
app.UseClientFiles();

app.MapTelemetry();
app.MapAnalyticsApi();
app.MapDemoApi();
app.MapSystemApi();
app.MapHistory();
app.MapLocalUsage();
app.MapMcp("/mcp");
app.MapClientFallback();

await app.RunAsync();

public partial class Program;
