using Npgsql;
using TokenScope.Api.Shared.Time;

namespace TokenScope.Api.Infrastructure.Database;

public static class ContinuousAggregateRefresher
{
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    public sealed record RefreshWindow(string View, DateTime Start, DateTime End);

    public static async Task RefreshAsync(
        NpgsqlDataSource database,
        DateTime from,
        CancellationToken cancellationToken)
    {
        await RefreshGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var window in RefreshWindows(from, DateTime.UtcNow))
            {
                // View names come only from the fixed list in RefreshWindows; bounds are parameters.
                var sql = $"CALL refresh_continuous_aggregate('{window.View}', $1::timestamptz, $2::timestamptz)";
                await ExecuteRefreshAsync(database, sql, [window.Start, window.End], cancellationToken);
            }
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    public static async Task RefreshDemoAsync(NpgsqlDataSource database, CancellationToken cancellationToken)
    {
        // Replacing demo data also removes old buckets. Refresh the full hourly/daily history,
        // stopping before the current bucket so the real-time tail remains visible.
        string[] statements =
        [
            "CALL refresh_continuous_aggregate('spend_1h', NULL, date_trunc('hour', now()))",
            "CALL refresh_continuous_aggregate('spend_1d', NULL, date_trunc('day', now()))",
            "CALL refresh_continuous_aggregate('spend_1m', date_trunc('minute', now() - INTERVAL '2 hours'), date_trunc('minute', now()))",
        ];

        await RefreshGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var statement in statements)
            {
                await ExecuteRefreshAsync(database, statement, [], cancellationToken);
            }
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    public static IEnumerable<RefreshWindow> RefreshWindows(DateTime from, DateTime now)
    {
        (string View, int BucketSeconds)[] aggregates =
        [
            ("spend_1m", 60),
            ("spend_1h", 3_600),
            ("spend_1d", 86_400),
        ];

        foreach (var (view, bucketSeconds) in aggregates)
        {
            // Minute charts cover one hour. Retain the two-hour policy window while refreshing
            // the complete imported history for hourly and daily charts.
            var earliest = view == "spend_1m" && from < now.AddHours(-2) ? now.AddHours(-2) : from;
            var start = TimeBuckets.Floor(earliest, bucketSeconds);
            var end = TimeBuckets.Floor(now, bucketSeconds);

            if (start < end)
            {
                yield return new RefreshWindow(view, start, end);
            }
        }
    }

    private static async Task ExecuteRefreshAsync(
        NpgsqlDataSource database,
        string sql,
        DateTime[] bounds,
        CancellationToken cancellationToken)
    {
        // Each refresh runs outside a transaction. Our process serializes refreshes, but Timescale's
        // scheduled jobs can still overlap them, so retry transient database conflicts as well.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var command = database.CreateCommand(sql);
                command.CommandTimeout = 300;

                foreach (var bound in bounds)
                {
                    command.Parameters.AddWithValue(bound);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }
            catch (PostgresException exception) when (
                attempt < 8 && exception.SqlState is "55P03" or "40001" or "40P01")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }
    }
}
