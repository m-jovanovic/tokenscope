using System.Diagnostics;
using Dapper;
using Npgsql;

namespace TokenScope.Api.Features.Analytics;

public static class GetSummary
{
    public static void MapEndpoint(IEndpointRouteBuilder api)
    {
        api.MapGet("/summary", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        NpgsqlDataSource database,
        string? source,
        string? range,
        CancellationToken cancellationToken)
    {
        var tier = UsageQueries.PickTier(range);
        var selectedSource = UsageQueries.PickSource(source);
        var end = DateTime.UtcNow;
        var start = end - tier.Window;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var totals = await connection.QuerySingleAsync<Totals>(new CommandDefinition(UsageQueries.WindowSql(tier) + """
            SELECT coalesce(sum(cost_usd_micros),0)::bigint AS CostUsdMicros,
                   coalesce(sum(requests),0)::bigint AS Requests,
                   coalesce(sum(input_tokens),0)::bigint AS InputTokens,
                   coalesce(sum(output_tokens),0)::bigint AS OutputTokens,
                   coalesce(sum(cache_read_tokens),0)::bigint AS CacheReadTokens,
                   coalesce(sum(cache_creation_tokens),0)::bigint AS CacheCreationTokens FROM windowed
            """, UsageQueries.WindowArgs(selectedSource, start, end, tier), cancellationToken: cancellationToken));
        stopwatch.Stop();

        var coverage = await connection.QuerySingleAsync<UsageCoverage>(new CommandDefinition("""
            SELECT count(cost_usd_micros) AS PricedRequests,
              count(*) FILTER (WHERE cost_usd_micros IS NULL) AS UnpricedRequests,
              count(*) FILTER (WHERE is_synthetic OR source='demo') AS SyntheticRequests
            FROM api_requests WHERE source=@source AND time>=@start AND time<@end
            """, new
        {
            source = selectedSource,
            start,
            end
        }, cancellationToken: cancellationToken));

        return Results.Ok(new Response(
            tier.View,
            stopwatch.Elapsed.TotalMilliseconds,
            totals,
            start,
            end,
            coverage.PricedRequests,
            coverage.UnpricedRequests,
            coverage.SyntheticRequests));
    }

    public sealed record Response(
        string ServedFrom,
        double QueryMs,
        Totals Totals,
        DateTime WindowStart,
        DateTime WindowEnd,
        long PricedRequests,
        long UnpricedRequests,
        long SyntheticRequests);

    public sealed record Totals(
        long CostUsdMicros,
        long Requests,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheCreationTokens);
}
