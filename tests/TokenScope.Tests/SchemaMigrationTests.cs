using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TokenScope.Api.Infrastructure.Database;
using Xunit;

namespace TokenScope.Tests;

[Collection("Database")]
public class SchemaMigrationTests
{
    [DatabaseFact]
    public async Task Concurrent_startup_and_partial_upgrade_preserve_events_and_refresh_policies()
    {
        var connectionString = Environment.GetEnvironmentVariable("TOKENSCOPE_TEST_CONNECTION")!;
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (!settings.Database!.StartsWith("tokenscope_test_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Requires a disposable test database.");
        }

        var schema = "migration_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {schema}");
        try
        {
            settings.SearchPath = $"{schema},public";
            await using var db = NpgsqlDataSource.Create(settings.ConnectionString);
            Task Migrate() => DatabaseSchema.EnsureCreatedAsync(db, NullLogger.Instance);
            await using (var gate = await admin.BeginTransactionAsync())
            {
                await admin.ExecuteAsync("SELECT pg_advisory_xact_lock(82014360114738)", transaction: gate);
                var starts = Enumerable.Range(0, 5).Select(_ => Migrate()).ToArray();
                await using var observer = await db.OpenConnectionAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (await observer.QuerySingleAsync<int>(new CommandDefinition("""
                    SELECT count(*) FROM pg_stat_activity WHERE datname=current_database()
                        AND wait_event='advisory' AND query LIKE '%pg_advisory_xact_lock(82014360114738)%'
                    """, cancellationToken: deadline.Token)) < starts.Length)
                {
                    Assert.DoesNotContain(starts, startup => startup.IsCompleted);
                    await Task.Delay(10, deadline.Token);
                }
                await gate.CommitAsync();
                await Task.WhenAll(starts);
            }
            await using var connection = await db.OpenConnectionAsync();
            await connection.ExecuteAsync("""
                INSERT INTO api_requests(time,source,developer,session_id,model,input_tokens,output_tokens,
                    cache_read_tokens,cache_creation_tokens,cost_usd_micros)
                VALUES (now(),'history','synthetic','upgrade','model',2,3,0,0,9);
                ALTER TABLE api_requests ALTER COLUMN cost_usd_micros SET NOT NULL;
                ALTER TABLE api_requests DROP COLUMN event_key;
                DROP TABLE local_usage_cursors;
                """);

            await Migrate();
            await Migrate();
            var cost = await connection.QuerySingleAsync<long>(
                "SELECT cost_usd_micros FROM api_requests WHERE session_id='upgrade' AND event_key IS NULL");
            Assert.Equal(9, cost);
            Assert.Equal("YES", await connection.QuerySingleAsync<string>("""
                SELECT is_nullable FROM information_schema.columns WHERE table_schema=@schema
                    AND table_name='api_requests' AND column_name='cost_usd_micros'
                """, new
            {
                schema
            }));
            Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT count(*) FROM local_usage_cursors"));
            Assert.Equal(3, await connection.QuerySingleAsync<int>("""
                SELECT count(*) FROM timescaledb_information.continuous_aggregates a
                JOIN timescaledb_information.jobs j ON j.hypertable_schema=a.view_schema AND j.hypertable_name=a.view_name
                WHERE a.view_schema=@schema AND j.proc_name='policy_refresh_continuous_aggregate'
                """, new
            {
                schema
            }));
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {schema} CASCADE");
        }
    }
}
