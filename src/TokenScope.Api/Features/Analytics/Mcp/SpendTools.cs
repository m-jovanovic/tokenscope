using System.ComponentModel;
using System.Data;
using System.Text.Json;
using Dapper;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Npgsql;
using TokenScope.Api.Shared.Time;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.Analytics.Mcp;

[McpServerToolType]
public sealed class SpendTools(NpgsqlDataSource database)
{
    private static readonly Dictionary<string, string> Groups = new()
    {
        ["model"] = "model",
        ["agent"] = "agent_name",
        ["skill"] = "skill_name",
        ["mcp_server"] = "mcp_server",
        ["developer"] = "developer",
    };

    [McpServerTool(Name = "get_spend_breakdown", ReadOnly = true)]
    [Description("API-equivalent spend grouped by model, agent, skill, mcp_server or developer. Missing attribution is unavailable; unpriced requests are excluded from dollar totals.")]
    public Task<string> GetSpendBreakdown(
        string groupBy = "model",
        int days = 7,
        string source = "live",
        CancellationToken cancellationToken = default)
    {
        Validate(days, source);
        if (!Groups.TryGetValue(groupBy, out var column))
        {
            throw new McpException("Unsupported group. Use model, agent, skill, mcp_server or developer.");
        }

        var end = DateTime.UtcNow;
        var tier = new UsageQueries.Tier("spend_1h", TimeSpan.FromDays(days), 3600);
        return QueryAsync(UsageQueries.WindowSql(tier) + $"""
            SELECT coalesce({column},'unavailable') AS label,
              round(coalesce(sum(cost_usd_micros),0)/1e6,2) AS estimated_usd,sum(requests)::bigint AS requests
            FROM windowed GROUP BY 1 ORDER BY 2 DESC LIMIT 20
            """, UsageQueries.WindowArgs(source, end.AddDays(-days), end, tier), source, end.AddDays(-days), end, cancellationToken);
    }

    [McpServerTool(Name = "get_daily_spend", ReadOnly = true)]
    [Description("API-equivalent estimated spend by UTC day over an exact rolling window; unknown costs excluded.")]
    public Task<string> GetDailySpend(int days = 30, string source = "live", CancellationToken cancellationToken = default)
    {
        Validate(days, source);
        var end = DateTime.UtcNow;
        var tier = new UsageQueries.Tier("spend_1d", TimeSpan.FromDays(days), 86400);
        return QueryAsync(UsageQueries.WindowSql(tier) + """
            SELECT bucket::date AS day,round(coalesce(sum(cost_usd_micros),0)/1e6,2) AS estimated_usd,
              sum(requests)::bigint AS requests FROM windowed GROUP BY 1 ORDER BY 1 LIMIT 200
            """, UsageQueries.WindowArgs(source, end.AddDays(-days), end, tier), source, end.AddDays(-days), end, cancellationToken);
    }

    [McpServerTool(Name = "find_spend_spikes", ReadOnly = true)]
    [Description("Completed UTC hours above a developer's own observed-hour average (z-score >3), plus the highest-spend agent. Unavailable attribution remains unavailable.")]
    public Task<string> FindSpendSpikes(int days = 30, string source = "demo", CancellationToken cancellationToken = default)
    {
        Validate(days, source);
        var end = TimeBuckets.Floor(DateTime.UtcNow, 3600);
        return QueryAsync("""
            WITH hourly AS (
              SELECT bucket,developer,sum(cost_usd_micros) AS cost FROM spend_1h
              WHERE source=@source AND bucket>=@start AND bucket<@end GROUP BY 1,2
            ),scored AS (
              SELECT bucket,developer,cost,(cost-avg(cost) OVER w)/nullif(stddev_samp(cost) OVER w,0) AS z
              FROM hourly WINDOW w AS (PARTITION BY developer)
            ),agent_totals AS (
              SELECT h.bucket,h.developer,coalesce(h.agent_name,'unavailable') AS agent,sum(h.cost_usd_micros) AS cost
              FROM spend_1h h JOIN scored s ON s.bucket=h.bucket AND s.developer=h.developer AND s.z>3
              WHERE h.source=@source GROUP BY 1,2,3
            ),top_agent AS (
              SELECT DISTINCT ON(bucket,developer) bucket,developer,agent FROM agent_totals
              ORDER BY bucket,developer,cost DESC,agent
            )
            SELECT s.bucket AS hour,s.developer,t.agent AS top_agent,
              round(s.cost/1e6,2) AS estimated_usd,round(s.z::numeric,1) AS z_score
            FROM scored s JOIN top_agent t USING(bucket,developer) ORDER BY s.z DESC LIMIT 10
            """, new
        {
            source,
            start = end.AddDays(-days),
            end
        }, source, end.AddDays(-days), end, cancellationToken);
    }

    [McpServerTool(Name = "run_report", ReadOnly = true)]
    [Description("Run a fixed report template: daily_spend, top_agents or spend_spikes. Other operations, including SQL, are rejected before database execution.")]
    public Task<string> RunReport(
        string report,
        int days = 30,
        string source = "demo",
        CancellationToken cancellationToken = default)
    {
        return report switch
        {
            "daily_spend" => GetDailySpend(days, source, cancellationToken),
            "top_agents" => GetSpendBreakdown("agent", days, source, cancellationToken),
            "spend_spikes" => FindSpendSpikes(days, source, cancellationToken),
            _ => throw new McpException("Unsupported report. Allowed: daily_spend, top_agents, spend_spikes. Arbitrary SQL is disabled."),
        };
    }

    // Retained only to make older client configurations fail safely and explain the replacement.
    [McpServerTool(Name = "run_readonly_sql", ReadOnly = true)]
    [Description("Deprecated. Always refuses arbitrary SQL; use run_report with a fixed report name instead.")]
    public string RunReadOnlySql(string sql)
    {
        throw new McpException("Arbitrary SQL is disabled. Use run_report: daily_spend, top_agents or spend_spikes.");
    }

    public static void Validate(int days, string source)
    {
        if (days is < 1 or > 90 || !UsageSources.IsActive(source))
        {
            throw new McpException("Days must be 1–90; source must be live, claude-local, history or demo.");
        }
    }

    private async Task<string> QueryAsync(
        string sql,
        object parameters,
        string source,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='5s'",
            transaction: transaction,
            cancellationToken: cancellationToken));

        try
        {
            var rows = await connection.QueryAsync(new CommandDefinition(
                sql, parameters, transaction, commandTimeout: 5, cancellationToken: cancellationToken));
            var coverage = await connection.QuerySingleAsync<UsageCoverage>(new CommandDefinition("""
                SELECT count(cost_usd_micros) AS PricedRequests,
                  count(*) FILTER (WHERE cost_usd_micros IS NULL) AS UnpricedRequests,
                  count(*) FILTER (WHERE is_synthetic OR source='demo') AS SyntheticRequests
                FROM api_requests WHERE source=@source AND time>=@start AND time<@end
                """, new
            {
                source,
                start,
                end
            }, transaction, commandTimeout: 5, cancellationToken: cancellationToken));

            return JsonSerializer.Serialize(new
            {
                source,
                windowStart = start,
                windowEnd = end,
                estimateNotice = "Dollar amounts are subtotals of priced requests, not billing. Unpriced requests contribute tokens/requests only. Synthetic requests are not personal usage. Attribution can be unavailable.",
                coverage = new
                {
                    pricedRequests = coverage.PricedRequests,
                    unpricedRequests = coverage.UnpricedRequests,
                    syntheticRequests = coverage.SyntheticRequests,
                },
                rows = rows.Select(row => (IDictionary<string, object?>)row),
            });
        }
        catch (NpgsqlException)
        {
            throw new McpException("Report unavailable: database failure or report timeout.");
        }
    }
}
