using System.Text.Json;
using Dapper;
using Npgsql;
using TokenScope.Api.Features.Diagnostics;
using TokenScope.Api.Features.LocalUsage;
using TokenScope.Api.Shared.Usage;
using Xunit;

namespace TokenScope.Tests;

[Collection("Database")]
public class LocalUsageDatabaseTests
{
    [DatabaseFact]
    public async Task Partial_updates_duplicate_files_restart_and_rollups_stay_idempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("TOKENSCOPE_TEST_CONNECTION")!;
        if (!new NpgsqlConnectionStringBuilder(connectionString).Database!.StartsWith("tokenscope_test_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Requires a disposable database.");
        }
        await using var db = NpgsqlDataSource.Create(connectionString);
        await using (var setup = db.CreateCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql"))))
        {
            await setup.ExecuteNonQueryAsync();
        }
        await using (var sentinel = await db.OpenConnectionAsync())
        {
            await sentinel.ExecuteAsync("""
                INSERT INTO api_requests(time,source,developer,session_id,model,input_tokens,output_tokens,
                    cache_read_tokens,cache_creation_tokens,event_key)
                VALUES(now()-interval '2 days','codex-local','retired-synthetic','retired-synthetic','retired-model',10,5,0,0,'retired-sentinel');
                INSERT INTO local_usage_cursors(source,path_hash,state) VALUES('codex-local','retired-sentinel','{"retired":true}');
                INSERT INTO local_request_state(source,event_key,payload) VALUES('codex-local','retired-sentinel','{"retired":true}');
                """);
        }
        // Startup remains additive: removing a collector must never delete its existing data.
        await using (var upgrade = db.CreateCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql"))))
        {
            await upgrade.ExecuteNonQueryAsync();
        }
        var store = new LocalUsageStore(db);
        Assert.Empty(await store.LoadCursorsAsync(CancellationToken.None));
        var cursor = new LocalCursor { Source = "claude-local", PathHash = UsageIdentity.Hash("synthetic-file-a"), Offset = 100, Length = 100 };
        var first = new ApiRequestRow(DateTime.UtcNow.Date.AddDays(-2).AddHours(12).AddTicks(7), "claude-local", "local-test", "hashed-test-session",
            "claude-haiku-4-5", null, null, null, null, 100, 10, 0, 0, 100, null, "local-test-key", "synthetic", true);
        var saved = await store.SaveAsync(cursor, [first], 0, CancellationToken.None);
        Assert.Equal(1, saved.Inserted);
        var final = first with
        {
            OutputTokens = 30,
            CostUsdMicros = 300
        };
        saved = await store.SaveAsync(saved.Cursor with
        {
            Offset = 200,
            Length = 200
        }, [final], 0, CancellationToken.None);
        Assert.Equal(1, saved.Updated);
        var restarted = new LocalUsageStore(db);
        var durable = (await restarted.LoadCursorsAsync(CancellationToken.None))[cursor.Source + ":" + cursor.PathHash];
        Assert.Equal(200, durable.Offset);
        Assert.Equal(1, durable.Inserted);
        Assert.Equal(1, durable.Updated);
        var replay = await restarted.SaveAsync(durable, [final, first], 0, CancellationToken.None);
        Assert.Equal(0, replay.Inserted + replay.Updated);
        var otherFile = cursor with
        {
            PathHash = UsageIdentity.Hash("synthetic-file-copy")
        };
        var duplicate = await restarted.SaveAsync(otherFile, [final], 0, CancellationToken.None);
        Assert.Equal(0, duplicate.Inserted + duplicate.Updated);
        Assert.True(await restarted.HasPendingRefreshAsync(CancellationToken.None));
        await restarted.RefreshPendingAsync(CancellationToken.None);
        Assert.False(await restarted.HasPendingRefreshAsync(CancellationToken.None));
        await using var conn = await db.OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM api_requests WHERE source='claude-local'"));
        Assert.Equal(first.Time.AddTicks(-7), await conn.ExecuteScalarAsync<DateTime>("SELECT time FROM api_requests WHERE event_key='local-test-key'"));
        Assert.Equal(30, await conn.ExecuteScalarAsync<long>("SELECT sum(output_tokens) FROM spend_1d WHERE source='claude-local'"));
        Assert.Equal(300, await conn.ExecuteScalarAsync<long>("SELECT sum(cost_usd_micros) FROM spend_1d WHERE source='claude-local'"));
        var state = await conn.ExecuteScalarAsync<string>("SELECT state::text FROM local_usage_cursors WHERE path_hash=@hash", new
        {
            hash = cursor.PathHash
        });
        Assert.DoesNotContain("synthetic-file", state);
        var concurrent = first with
        {
            EventKey = "concurrent-new-key"
        };
        var writes = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => restarted.SaveAsync(
            cursor with
            {
                PathHash = UsageIdentity.Hash("parallel-file-" + i)
            }, [concurrent], 0, CancellationToken.None)));
        Assert.Equal(1, writes.Sum(w => w.Inserted));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM api_requests WHERE source='claude-local' AND event_key='concurrent-new-key'"));

        // Reproduce an older ledger with 100ns precision and its JSON timestamp rounded UP in PostgreSQL.
        // Include the duplicate left by the former DELETE bug, then verify one update repairs both rows.
        var legacy = first with
        {
            EventKey = "legacy-fractional-key",
            OutputTokens = 5,
            CostUsdMicros = 50
        };
        var legacySaved = await restarted.SaveAsync(cursor with
        {
            PathHash = UsageIdentity.Hash("legacy-fractional-file")
        }, [legacy], 0, CancellationToken.None);
        await conn.ExecuteAsync("""
            UPDATE api_requests SET time=CAST(@timeText AS timestamptz) WHERE event_key=@key;
            UPDATE local_request_state SET payload=CAST(@payload AS jsonb) WHERE event_key=@key;
            INSERT INTO api_requests SELECT * FROM api_requests WHERE event_key=@key;
            """, new
        {
            timeText = legacy.Time.ToString("O"),
            key = legacy.EventKey,
            payload = JsonSerializer.Serialize(legacy)
        });
        Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM api_requests WHERE event_key=@key", new
        {
            key = legacy.EventKey
        }));
        Assert.Equal(legacy.Time.AddTicks(3), await conn.ExecuteScalarAsync<DateTime>("SELECT min(time) FROM api_requests WHERE event_key=@key", new
        {
            key = legacy.EventKey
        }));
        var legacyFinal = legacy with
        {
            OutputTokens = 25,
            CostUsdMicros = 250
        };
        var repaired = await restarted.SaveAsync(legacySaved.Cursor, [legacyFinal], 0, CancellationToken.None);
        Assert.Equal(1, repaired.Updated);
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM api_requests WHERE event_key=@key", new
        {
            key = legacy.EventKey
        }));
        await restarted.RefreshPendingAsync(CancellationToken.None);

        // A later-discovered partial can move the canonical request into an earlier bucket.
        var earlier = legacyFinal with
        {
            Time = legacy.Time.AddDays(-1),
            OutputTokens = 35,
            CostUsdMicros = 350
        };
        var moved = await restarted.SaveAsync(repaired.Cursor, [earlier], 0, CancellationToken.None);
        Assert.Equal(1, moved.Updated);
        Assert.Equal(earlier.Time.AddTicks(-7), await conn.ExecuteScalarAsync<DateTime>("SELECT time FROM api_requests WHERE event_key=@key", new
        {
            key = legacy.EventKey
        }));
        await restarted.RefreshPendingAsync(CancellationToken.None);
        Assert.Equal(3, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM api_requests WHERE source='claude-local'"));
        Assert.Equal(75, await conn.ExecuteScalarAsync<long>("SELECT sum(output_tokens) FROM spend_1d WHERE source='claude-local'"));
        Assert.Equal(750, await conn.ExecuteScalarAsync<long>("SELECT sum(cost_usd_micros) FROM spend_1d WHERE source='claude-local'"));
        Assert.DoesNotContain(await GetSystemStatus.ReadSourcesAsync(conn, CancellationToken.None), s => s.Source == "codex-local");
        Assert.All((await restarted.LoadCursorsAsync(CancellationToken.None)).Values, c => Assert.Equal("claude-local", c.Source));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM api_requests WHERE source='codex-local' AND event_key='retired-sentinel'"));
        Assert.Equal("{\"retired\": true}", await conn.ExecuteScalarAsync<string>("SELECT state::text FROM local_usage_cursors WHERE source='codex-local' AND path_hash='retired-sentinel'"));
        Assert.Equal("{\"retired\": true}", await conn.ExecuteScalarAsync<string>("SELECT payload::text FROM local_request_state WHERE source='codex-local' AND event_key='retired-sentinel'"));
    }
}
