using Dapper;
using Npgsql;

namespace TokenScope.Api.Features.Analytics;

public static class GetSessions
{
    public static void MapEndpoint(IEndpointRouteBuilder api)
    {
        api.MapGet("/sessions", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        NpgsqlDataSource database,
        string? source,
        string? range,
        string? metric,
        CancellationToken cancellationToken)
    {
        var end = DateTime.UtcNow;
        var order = metric == "requests" ? "Requests" : "CostUsdMicros";
        await using var connection = await database.OpenConnectionAsync(cancellationToken);

        var sessions = await connection.QueryAsync<Response>(new CommandDefinition($"""
            SELECT session_id AS SessionId,developer AS Developer,min(time) AS StartedAt,max(time) AS EndedAt,
              count(*) AS Requests,coalesce(sum(cost_usd_micros),0)::bigint AS CostUsdMicros,
              count(cost_usd_micros) AS PricedRequests,count(*) FILTER(WHERE cost_usd_micros IS NULL) AS UnpricedRequests
            FROM api_requests WHERE source=@source AND time>=@start AND time<@end
            GROUP BY session_id,developer ORDER BY {order} DESC LIMIT 8
            """, new
        {
            source = UsageQueries.PickSource(source),
            start = end - UsageQueries.PickTier(range).Window,
            end,
        }, cancellationToken: cancellationToken));

        return Results.Ok(sessions);
    }

    public sealed record Response(
        string SessionId,
        string Developer,
        DateTime StartedAt,
        DateTime EndedAt,
        long Requests,
        long CostUsdMicros,
        long PricedRequests,
        long UnpricedRequests);
}
