using System.Text.Json;
using TokenScope.Api.Features.History;
using TokenScope.Api.Shared.Usage;
using Xunit;

namespace TokenScope.Tests;

public class HistoryTests
{
    [Fact]
    public void Fixture_collapses_streaming_snapshots_preserves_unknown_cost_and_drops_content()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "claude-history.synthetic.jsonl"));
        var result = HistoryParser.Parse(fixture);
        Assert.Equal(7, result.Records);
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(2, result.Skipped);
        Assert.True(result.Synthetic);
        Assert.All(result.Rows, r => Assert.True(r.IsSynthetic));
        var sonnet = Assert.Single(result.Rows, r => r.Model.StartsWith("claude-sonnet"));
        Assert.Equal(200, sonnet.OutputTokens);
        Assert.Equal(14700, sonnet.CostUsdMicros); // 3000 input + 3000 output + 1200 read + 7500 write.
        Assert.Null(Assert.Single(result.Rows, r => r.Model.Contains("unknown")).CostUsdMicros);
        Assert.Equal(12300, Assert.Single(result.Rows, r => r.Model.Contains("opus")).CostUsdMicros);
        var stored = JsonSerializer.Serialize(result.Rows);
        Assert.DoesNotContain("MUST NEVER BE STORED", stored);
        Assert.DoesNotContain("synthetic-session", stored);
        Assert.All(result.Rows, r =>
        {
            Assert.Null(r.AgentName);
            Assert.Null(r.SkillName);
            Assert.Null(r.McpServer);
        });
        Assert.Equal(result.Rows.Select(UsageIdentity.Key), HistoryParser.Parse(fixture).Rows.Select(UsageIdentity.Key));
    }

    [Theory]
    [InlineData("claude-haiku-4-5", 0, null, 1500L)]
    [InlineData("claude-haiku-4-5", 1000, null, null)]
    [InlineData("claude-next-future", 0, null, null)]
    [InlineData("claude-next-future", 0, 0.02, 20000L)]
    public void Pricing_does_not_guess_unknown_model_or_cache_duration(string model, int cache, double? reported, long? expected)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId = "s",
            requestId = "r",
            timestamp = "2026-10-01T00:00:00Z",
            costUSD = reported,
            message = new
            {
                id = "m",
                model,
                usage = new
                {
                    input_tokens = 1000,
                    output_tokens = 100,
                    cache_creation_input_tokens = cache
                }
            }
        });
        Assert.Equal(expected, Assert.Single(HistoryParser.Parse(json).Rows).CostUsdMicros);
    }

    [Fact]
    public void Invalid_and_private_records_produce_only_sanitized_issues()
    {
        var result = HistoryParser.Parse("""
            {"type":"assistant","sessionId":"secret-user","timestamp":"bad-private-value","message":{"id":"private","model":"claude-haiku-4-5","usage":{"input_tokens":-1,"output_tokens":2}}}
            {"secret":"private prompt"
            """);
        Assert.Empty(result.Rows);
        Assert.Equal(2, result.Skipped);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void A_repeated_request_id_with_multiple_message_ids_is_one_request()
    {
        string Record(string id, int output) => JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId = "s",
            requestId = "r",
            timestamp = "2026-10-01T00:00:00Z",
            message = new
            {
                id,
                model = "claude-haiku-4-5",
                usage = new
                {
                    input_tokens = 1000,
                    output_tokens = output
                }
            }
        });
        var parsed = HistoryParser.Parse(Record("m1", 20) + "\n" + Record("m2", 100));
        Assert.Single(parsed.Rows);
        Assert.Equal(100, parsed.Rows[0].OutputTokens);
        Assert.Equal(HistoryParser.Parse(Record("different-message", 100)).Rows[0].EventKey, parsed.Rows[0].EventKey);
    }

    [Fact]
    public void Reported_microdollars_above_one_dollar_are_preserved()
    {
        var parsed = HistoryParser.Parse("""
            {"type":"assistant","sessionId":"s","requestId":"r","timestamp":"2026-10-01T00:00:00Z","cost_usd_micros":2500000,"message":{"id":"m","model":"claude-unknown","usage":{"input_tokens":1,"output_tokens":1}}}
            """);
        Assert.Equal(2_500_000, Assert.Single(parsed.Rows).CostUsdMicros);
    }
}
