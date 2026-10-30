using TokenScope.Api.Shared.Time;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.Analytics;

public static class UsageQueries
{
    public sealed record Tier(string View, TimeSpan Window, int BucketSeconds);

    public static Tier PickTier(string? range) => range switch
    {
        null or "30d" => new("spend_1d", TimeSpan.FromDays(30), 86400),
        "1h" => new("spend_1m", TimeSpan.FromHours(1), 60),
        "24h" => new("spend_1h", TimeSpan.FromHours(24), 3600),
        "7d" => new("spend_1h", TimeSpan.FromDays(7), 3600),
        "90d" => new("spend_1d", TimeSpan.FromDays(90), 86400),
        _ => throw new InvalidDataException("Range must be 1h, 24h, 7d, 30d or 90d."),
    };

    public static string PickSource(string? source) => source switch
    {
        null => "live",
        _ when UsageSources.IsActive(source) => source,
        _ => throw new InvalidDataException("Unknown usage source."),
    };

    public static object WindowArgs(string source, DateTime start, DateTime end, Tier tier) => new
    {
        source,
        start,
        end,
        fullStart = TimeBuckets.Ceiling(start, tier.BucketSeconds),
        fullEnd = TimeBuckets.Floor(end, tier.BucketSeconds),
        step = TimeSpan.FromSeconds(tier.BucketSeconds),
    };

    // Full buckets come from the continuous aggregate; only the two partial boundaries read raw events.
    // This keeps exact [start,end) semantics and avoids dropping the first partial hour/day.
    public static string WindowSql(Tier tier)
    {
        var attribution = tier.View == "spend_1m"
            ? "NULL::text AS query_source, NULL::text AS agent_name, NULL::text AS skill_name, NULL::text AS mcp_server"
            : "query_source, agent_name, skill_name, mcp_server";

        return $"""
            WITH windowed AS (
              SELECT bucket, developer, model, {attribution}, requests, cost_usd_micros,
                     input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens
              FROM {tier.View}
              WHERE source = @source AND bucket >= @fullStart AND bucket < @fullEnd
              UNION ALL
              SELECT time_bucket(@step::interval, time), developer, model, query_source, agent_name, skill_name,
                     mcp_server, 1::bigint, cost_usd_micros, input_tokens::bigint, output_tokens::bigint,
                     cache_read_tokens::bigint, cache_creation_tokens::bigint
              FROM api_requests
              WHERE source = @source AND time >= @start AND time < @end
                AND (time < @fullStart OR time >= @fullEnd)
            )
            """;
    }
}

public sealed record UsageCoverage(long PricedRequests, long UnpricedRequests, long SyntheticRequests);

public sealed record UsagePoint(DateTime Bucket, string Model, long CostUsdMicros, long Requests);
