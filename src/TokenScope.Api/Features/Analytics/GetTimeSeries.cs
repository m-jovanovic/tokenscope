using System.Diagnostics;
using Dapper;
using Npgsql;

namespace TokenScope.Api.Features.Analytics;

public static class GetTimeSeries
{
    public static void MapEndpoint(IEndpointRouteBuilder api)
    {
        api.MapGet("/timeseries", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        NpgsqlDataSource database,
        string? source,
        string? range,
        CancellationToken cancellationToken)
    {
        var tier = UsageQueries.PickTier(range);
        var end = DateTime.UtcNow;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var points = await connection.QueryAsync<UsagePoint>(new CommandDefinition(UsageQueries.WindowSql(tier) + """
            SELECT bucket AS Bucket, model AS Model, coalesce(sum(cost_usd_micros),0)::bigint AS CostUsdMicros,
              sum(requests)::bigint AS Requests FROM windowed GROUP BY bucket,model ORDER BY bucket
            """, UsageQueries.WindowArgs(UsageQueries.PickSource(source), end - tier.Window, end, tier), cancellationToken: cancellationToken));

        return Results.Ok(new Response(
            tier.View,
            tier.BucketSeconds,
            stopwatch.Elapsed.TotalMilliseconds,
            end - tier.Window,
            end,
            points));
    }

    public sealed record Response(
        string ServedFrom,
        int BucketSeconds,
        double QueryMs,
        DateTime WindowStart,
        DateTime WindowEnd,
        IEnumerable<UsagePoint> Points);
}
