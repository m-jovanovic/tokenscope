using System.Text.Json;
using ModelContextProtocol;
using Npgsql;
using TokenScope.Api.Features.Analytics;
using TokenScope.Api.Features.Analytics.Mcp;
using TokenScope.Api.Features.Telemetry;
using TokenScope.Api.Infrastructure.Database;
using TokenScope.Api.Shared.Time;
using TokenScope.Api.Shared.Usage;
using Xunit;

namespace TokenScope.Tests;

public class IngestionAndQueryTests
{
    private static ApiRequestRow Row(int i) => new(DateTime.UnixEpoch, "live", "test", "session", "test-model",
        null, null, null, null, 1, 1, 0, 0, null, null, i.ToString());

    [Fact]
    public void Concurrent_batches_are_accepted_all_or_nothing()
    {
        var queue = new IngestQueue();
        var accepted = 0;
        Parallel.For(0, 100, i =>
        {
            if (queue.TryEnqueueAll(Enumerable.Range(i * 1000, 1000).Select(Row).ToArray()))
            {
                Interlocked.Increment(ref accepted);
            }
        });
        Assert.Equal(50, accepted);
        Assert.Equal(50_000, queue.Count);
        Assert.Equal(50_000, queue.Accepted);
        Assert.Equal(50_000, queue.Rejected);
    }

    [Fact]
    public void Exact_window_boundaries_keep_partial_buckets_and_future_exclusion()
    {
        var instant = new DateTime(2026, 10, 20, 12, 34, 56, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), TimeBuckets.Floor(instant, 3600));
        Assert.Equal(new DateTime(2026, 10, 20, 13, 0, 0, DateTimeKind.Utc), TimeBuckets.Ceiling(instant, 3600));
        Assert.Equal(instant.Date, TimeBuckets.Ceiling(instant.Date, 86400));
        Assert.Throws<InvalidDataException>(() => UsageQueries.PickSource("everything"));
        Assert.Throws<InvalidDataException>(() => UsageQueries.PickTier("100years"));
    }

    [Fact]
    public void Otlp_preserves_unpriced_and_drops_unrelated_events()
    {
        using var doc = JsonDocument.Parse("""
            {"resourceLogs":[{"scopeLogs":[{"logRecords":[
              {"timeUnixNano":"1792411200000000000","body":{"stringValue":"claude_code.api_request"},"attributes":[
                {"key":"model","value":{"stringValue":"claude-haiku-4-5"}},
                {"key":"input_tokens","value":{"intValue":"100"}},
                {"key":"prompt","value":{"stringValue":"PRIVATE PROMPT"}}]},
              {"body":{"stringValue":"claude_code.user_prompt"},"attributes":[]}
            ]}]}]}
            """);
        var row = Assert.Single(OtlpMapper.MapApiRequests(doc.RootElement, "test"));
        Assert.Null(row.CostUsdMicros);
        Assert.Null(row.AgentName);
        Assert.Equal(100, row.InputTokens);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(row));
    }

    [Fact]
    public async Task Sql_and_unknown_templates_are_refused_without_a_database_connection()
    {
        await using var db = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unreachable;Username=test");
        var tools = new SpendTools(db);
        Assert.Throws<McpException>(() => tools.RunReadOnlySql("COMMIT; DELETE FROM api_requests"));
        await Assert.ThrowsAsync<McpException>(() => tools.RunReport("delete_data"));
        Assert.Throws<McpException>(() => SpendTools.Validate(1000, "live"));
        Assert.Throws<InvalidDataException>(() => UsageQueries.PickSource("codex-local"));
        await Assert.ThrowsAsync<McpException>(() => tools.GetDailySpend(source: "codex-local"));
        await Assert.ThrowsAsync<McpException>(() => tools.GetSpendBreakdown(source: "codex-local"));
        await Assert.ThrowsAsync<McpException>(() => tools.FindSpendSpikes(source: "codex-local"));
        Assert.Equal(new[] { "live", "claude-local", "history", "demo" }, UsageSources.Active);
        foreach (var source in UsageSources.Active)
        {
            Assert.Equal(source, UsageQueries.PickSource(source));
            SpendTools.Validate(7, source);
        }
    }

    [Fact]
    public async Task Refresh_skips_ranges_with_no_completed_buckets()
    {
        await using var db = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unreachable;Username=test");
        // An accepted clock-skewed timestamp has no completed minute/hour/day to materialize.
        await ContinuousAggregateRefresher.RefreshAsync(db, DateTime.UtcNow.AddMinutes(1), CancellationToken.None);
    }
}
