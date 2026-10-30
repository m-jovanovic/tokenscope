using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TokenScope.Api.Features.LocalUsage;
using TokenScope.Api.Shared.Usage;
using Xunit;

namespace TokenScope.Tests;

public class LocalUsageTests
{
    [Fact]
    public async Task Incomplete_utf8_line_is_retried_from_last_completed_byte_offset()
    {
        using var stream = new MemoryStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"a\":\"é\"}\r\n{\"unfinished\":"));
        var first = new List<LocalLineReader.Line>();
        await foreach (var line in LocalLineReader.ReadAsync(stream, 0))
        {
            first.Add(line);
        }
        Assert.Single(first);
        Assert.Equal("{\"a\":\"é\"}", first[0].Text);
        stream.Seek(0, SeekOrigin.End);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("true}\n"));
        var appended = new List<LocalLineReader.Line>();
        await foreach (var line in LocalLineReader.ReadAsync(stream, first[0].NextOffset))
        {
            appended.Add(line);
        }
        Assert.Equal("{\"unfinished\":true}", Assert.Single(appended).Text);
        Assert.Equal(stream.Length, appended[0].NextOffset);
    }

    [Fact]
    public void Claude_snapshots_use_stable_hashed_identity_and_do_not_store_content()
    {
        string Snapshot(int output) => JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId = "PRIVATE-SESSION",
            requestId = "PRIVATE-REQUEST",
            timestamp = "2026-10-20T10:00:00Z",
            message = new
            {
                id = "PRIVATE-MESSAGE",
                model = "claude-opus-5-5",
                content = new[] { new { text = "PRIVATE CONTENT" } },
                usage = new
                {
                    input_tokens = 100,
                    output_tokens = output,
                    cache_read_input_tokens = 1000,
                    cache_creation_input_tokens = 0,
                    inference_geo = "not_available",
                    service_tier = "standard"
                }
            }
        });
        var first = ClaudeUsageParser.ParseLine(Snapshot(10))!;
        var final = ClaudeUsageParser.ParseLine(Snapshot(50))!;
        Assert.NotNull(first);
        Assert.Equal(first.EventKey, final.EventKey);
        var merged = LocalUsageStore.Merge(first, final);
        Assert.Equal(50, merged.OutputTokens);
        Assert.Equal("claude-local", merged.Source);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(merged));
        Assert.Equal(merged, LocalUsageStore.Merge(merged, first));
        Assert.Equal(merged, LocalUsageStore.Merge(merged, final));
    }

    [Fact]
    public void Unknown_cost_is_not_converted_to_zero_by_update()
    {
        var first = new ApiRequestRow(DateTime.UtcNow, "claude-local", "local-user", "hashed-session", "unknown-model",
            null, null, null, null, 10, 20, 0, 0, null, null, "stable-key", "unpriced:unknown-model");
        var next = first with
        {
            OutputTokens = 30
        };
        var merged = LocalUsageStore.Merge(first, next);
        Assert.Null(merged.CostUsdMicros);
        Assert.Equal(30, merged.OutputTokens);
    }

    [Fact]
    public void Invalid_durable_cursor_is_reset_without_exposing_its_contents()
    {
        var cursor = LocalUsageStore.ReadCursor("claude-local", "hashed-file", "{\"Offset\":\"PRIVATE-CONTENT\"}");
        Assert.Equal(0, cursor.Offset);
        Assert.Equal("claude-local", cursor.Source);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(cursor));
    }

    [Fact]
    public async Task Retired_sources_are_rejected_before_storage_and_collector_lists_only_claude()
    {
        await using var db = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unreachable;Username=test");
        var store = new LocalUsageStore(db);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            new LocalCursor { Source = "codex-local", PathHash = "retired" }, [], 0, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => LocalUsageStore.ReadCursor("codex-local", "retired", "{}"));
        using var collector = new LocalUsageCollector(Options.Create(new LocalUsageOptions()), store, NullLogger<LocalUsageCollector>.Instance);
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(collector.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var client = Assert.Single(snapshot.RootElement.GetProperty("clients").EnumerateArray());
        Assert.Equal("claude-local", client.GetProperty("source").GetString());
        Assert.False(collector.RequestSync());
        Assert.Null(ClaudeUsageParser.ParseLine("""{"type":"token_usage_record","input_tokens":100,"output_tokens":20}"""));
    }
}
