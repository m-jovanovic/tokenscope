using System.Diagnostics;
using Npgsql;

namespace TokenScope.Api.Features.Demo;

public static class SeedDemo
{
    private static readonly SemaphoreSlim SeedGate = new(1);

    public static void MapEndpoint(IEndpointRouteBuilder demo)
    {
        demo.MapPost("/seed", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        NpgsqlDataSource database,
        int? developers,
        int? days,
        CancellationToken cancellationToken)
    {
        if (!await SeedGate.WaitAsync(0, cancellationToken))
        {
            return Results.Conflict(new ErrorResponse("Demo generation is already running."));
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var inserted = await DemoDataSeeder.SeedAsync(database, developers ?? 25, days ?? 90, cancellationToken);
            return Results.Ok(new Response(inserted, stopwatch.Elapsed.TotalSeconds));
        }
        finally
        {
            SeedGate.Release();
        }
    }

    public sealed record Response(long Inserted, double Seconds);

    public sealed record ErrorResponse(string Error);
}
