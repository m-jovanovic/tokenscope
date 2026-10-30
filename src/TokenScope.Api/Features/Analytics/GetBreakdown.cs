using Dapper;
using Npgsql;

namespace TokenScope.Api.Features.Analytics;

public static class GetBreakdown
{
    private static readonly Dictionary<string, string> Dimensions = new()
    {
        ["model"] = "model",
        ["agent"] = "agent_name",
        ["skill"] = "skill_name",
        ["mcp"] = "mcp_server",
        ["developer"] = "developer",
        ["query"] = "query_source",
    };

    public static void MapEndpoint(IEndpointRouteBuilder api)
    {
        api.MapGet("/breakdown", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        NpgsqlDataSource database,
        string? source,
        string? range,
        string? by,
        string? metric,
        CancellationToken cancellationToken)
    {
        var tier = UsageQueries.PickTier(range);
        if (tier.View == "spend_1m")
        {
            tier = tier with
            {
                View = "spend_1h",
                BucketSeconds = 3600
            };
        }

        if (!Dimensions.TryGetValue(by ?? "agent", out var column))
        {
            throw new InvalidDataException("Unknown breakdown dimension.");
        }

        var end = DateTime.UtcNow;
        var selectedSource = UsageQueries.PickSource(source);
        var order = metric == "requests" ? "3" : "2";
        await using var connection = await database.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<Subtotal>(new CommandDefinition(UsageQueries.WindowSql(tier) + $"""
            SELECT coalesce({column},'unavailable') AS Label,
              coalesce(sum(cost_usd_micros),0)::bigint AS CostUsdMicros, sum(requests)::bigint AS Requests
            FROM windowed GROUP BY 1 ORDER BY {order} DESC LIMIT 8
            """, UsageQueries.WindowArgs(selectedSource, end - tier.Window, end, tier), cancellationToken: cancellationToken));

        var coverage = (await connection.QueryAsync<DimensionCoverage>(new CommandDefinition($"""
            SELECT coalesce({column},'unavailable') AS Label,count(cost_usd_micros) AS PricedRequests,
              count(*) FILTER(WHERE cost_usd_micros IS NULL) AS UnpricedRequests
            FROM api_requests WHERE source=@source AND time>=@start AND time<@end GROUP BY 1
            """, new
        {
            source = selectedSource,
            start = end - tier.Window,
            end
        }, cancellationToken: cancellationToken)))
            .ToDictionary(row => row.Label);

        return Results.Ok(rows.Select(row => new Response(
            row.Label,
            row.CostUsdMicros,
            row.Requests,
            coverage.GetValueOrDefault(row.Label)?.PricedRequests ?? 0,
            coverage.GetValueOrDefault(row.Label)?.UnpricedRequests ?? 0)));
    }

    public sealed record Response(
        string Label,
        long CostUsdMicros,
        long Requests,
        long PricedRequests,
        long UnpricedRequests);

    private sealed record Subtotal(string Label, long CostUsdMicros, long Requests);

    private sealed record DimensionCoverage(string Label, long PricedRequests, long UnpricedRequests);
}
