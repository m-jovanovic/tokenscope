using Dapper;
using Npgsql;
using TokenScope.Api.Features.Demo;
using TokenScope.Api.Features.History;
using TokenScope.Api.Infrastructure.Database;
using TokenScope.Api.Shared.Usage;
using Xunit;

namespace TokenScope.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOKENSCOPE_TEST_CONNECTION")))
        {
            Skip = "Requires an explicitly supplied disposable TimescaleDB database.";
        }
    }
}

[Collection("Database")]
public class DatabaseIntegrationTests
{
    [DatabaseFact]
    public async Task Historical_import_refresh_and_replay_work_against_timescaledb()
    {
        var connectionString = Environment.GetEnvironmentVariable("TOKENSCOPE_TEST_CONNECTION")!;
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (!settings.Database!.StartsWith("tokenscope_test_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Integration tests require a disposable database named tokenscope_test_*.");
        }
        await using var db = NpgsqlDataSource.Create(connectionString);
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql");
        await using (var setup = db.CreateCommand(await File.ReadAllTextAsync(schemaPath)))
        {
            await setup.ExecuteNonQueryAsync();
        }
        var parsed = HistoryParser.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "claude-history.synthetic.jsonl")));
        var store = new UsageEventWriter(db);
        Assert.Equal(4, await store.InsertAsync(parsed.Rows.ToArray(), CancellationToken.None));
        await ContinuousAggregateRefresher.RefreshAsync(db, parsed.Rows.Min(r => r.Time), CancellationToken.None);
        Assert.Equal(0, await store.InsertAsync(parsed.Rows.ToArray(), CancellationToken.None));
        await ContinuousAggregateRefresher.RefreshAsync(db, parsed.Rows.Min(r => r.Time), CancellationToken.None);
        await using var conn = await db.OpenConnectionAsync();
        Assert.Equal(4, await conn.ExecuteScalarAsync<long>("SELECT sum(requests) FROM spend_1d WHERE source='history'"));
        Assert.Equal(28000, await conn.ExecuteScalarAsync<long>("SELECT sum(cost_usd_micros) FROM spend_1d WHERE source='history'"));
        var now = DateTime.UtcNow;
        var current = new ApiRequestRow(now.AddMinutes(-10), "history", "synthetic", "current-session", "claude-haiku-4-5",
            null, null, null, null, 100, 50, 0, 0, 350, null, "current-regression", "synthetic", true);
        Assert.Equal(1, await store.InsertAsync([current], CancellationToken.None));
        await ContinuousAggregateRefresher.RefreshAsync(db, parsed.Rows.Min(r => r.Time), CancellationToken.None);
        Assert.Equal(5, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM api_requests WHERE source='history'"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT sum(requests) FROM spend_1m WHERE source='history' AND bucket>=@start", new
        {
            start = now.AddHours(-1)
        }));
        Assert.Equal(50, await conn.ExecuteScalarAsync<long>("SELECT sum(output_tokens) FROM spend_1m WHERE source='history' AND bucket>=@start", new
        {
            start = now.AddHours(-1)
        }));
        var demoRows = await DemoDataSeeder.SeedAsync(db, 1, 2, CancellationToken.None);
        Assert.True(demoRows > 0);
    }
}
