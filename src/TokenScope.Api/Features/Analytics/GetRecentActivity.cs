using Dapper;
using Npgsql;

namespace TokenScope.Api.Features.Analytics;

public static class GetRecentActivity
{
    public static void MapEndpoint(IEndpointRouteBuilder api)
    {
        api.MapGet("/live", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        NpgsqlDataSource database,
        string? source,
        CancellationToken cancellationToken)
    {
        var selectedSource = UsageQueries.PickSource(source);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var end = DateTime.UtcNow;
        var tier = UsageQueries.PickTier("1h");

        var minutes = await connection.QueryAsync<UsagePoint>(new CommandDefinition(UsageQueries.WindowSql(tier) + """
            SELECT bucket AS Bucket,model AS Model,coalesce(sum(cost_usd_micros),0)::bigint AS CostUsdMicros,
              sum(requests)::bigint AS Requests FROM windowed GROUP BY bucket,model ORDER BY bucket
            """, UsageQueries.WindowArgs(selectedSource, end.AddHours(-1), end, tier), cancellationToken: cancellationToken));

        var today = await connection.QuerySingleAsync<Today>(new CommandDefinition("""
            SELECT 'today' AS Label,coalesce(sum(cost_usd_micros),0)::bigint AS CostUsdMicros,count(*) AS Requests,
              count(cost_usd_micros) AS PricedRequests,count(*) FILTER(WHERE cost_usd_micros IS NULL) AS UnpricedRequests
            FROM api_requests WHERE source=@source AND time>=@start AND time<@end
            """, new
        {
            source = selectedSource,
            start = end.Date,
            end
        }, cancellationToken: cancellationToken));

        var lastEventAt = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT max(time) FROM api_requests WHERE source=@source",
            new
            {
                source = selectedSource
            },
            cancellationToken: cancellationToken));

        return Results.Ok(new Response(minutes, today, lastEventAt, "UTC", selectedSource));
    }

    public sealed record Response(
        IEnumerable<UsagePoint> Minutes,
        Today Today,
        DateTime? LastEventAt,
        string DayBasis,
        string Source);

    public sealed record Today(
        string Label,
        long CostUsdMicros,
        long Requests,
        long PricedRequests,
        long UnpricedRequests);
}
