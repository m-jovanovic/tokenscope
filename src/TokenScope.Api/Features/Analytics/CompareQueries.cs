using System.Data;
using System.Diagnostics;
using Dapper;
using Npgsql;

namespace TokenScope.Api.Features.Analytics;

public static class CompareQueries
{
    public static void MapEndpoint(IEndpointRouteBuilder api)
    {
        api.MapPost("/bench", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        NpgsqlDataSource database,
        string? source,
        CancellationToken cancellationToken)
    {
        var end = DateTime.UtcNow.Date;
        var start = end.AddDays(-90);
        var parameters = new
        {
            source = UsageQueries.PickSource(source),
            start,
            end
        };

        const string rawSql = """
            SELECT time_bucket(INTERVAL '1 day',time) AS Bucket,model AS Model,
              coalesce(sum(cost_usd_micros),0)::bigint AS CostUsdMicros,count(*) AS Requests
            FROM api_requests WHERE source=@source AND time>=@start AND time<@end GROUP BY 1,2 ORDER BY 1,2
            """;
        const string rollupSql = """
            SELECT bucket AS Bucket,model AS Model,coalesce(sum(cost_usd_micros),0)::bigint AS CostUsdMicros,
              sum(requests)::bigint AS Requests FROM spend_1d
            WHERE source=@source AND bucket>=@start AND bucket<@end GROUP BY 1,2 ORDER BY 1,2
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        async Task<UsagePoint[]> ReadAsync(string sql)
        {
            var points = await connection.QueryAsync<UsagePoint>(new CommandDefinition(
                sql,
                parameters,
                transaction,
                commandTimeout: 30,
                cancellationToken: cancellationToken));

            return points.ToArray();
        }

        var rawResult = await ReadAsync(rawSql);
        var rollupResult = await ReadAsync(rollupSql);
        var rawTimes = new List<double>();
        var rollupTimes = new List<double>();

        async Task MeasureAsync(string sql, List<double> times)
        {
            var stopwatch = Stopwatch.StartNew();
            await ReadAsync(sql);
            times.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        // Warm each query once above, then alternate their measured order to reduce bias.
        for (var sample = 0; sample < 5; sample++)
        {
            if (sample % 2 == 0)
            {
                await MeasureAsync(rawSql, rawTimes);
                await MeasureAsync(rollupSql, rollupTimes);
            }
            else
            {
                await MeasureAsync(rollupSql, rollupTimes);
                await MeasureAsync(rawSql, rawTimes);
            }
        }

        var rawRows = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM api_requests WHERE source=@source AND time>=@start AND time<@end",
            parameters,
            transaction,
            cancellationToken: cancellationToken));
        var rollupRows = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM spend_1d WHERE source=@source AND bucket>=@start AND bucket<@end",
            parameters,
            transaction,
            cancellationToken: cancellationToken));

        rawTimes.Sort();
        rollupTimes.Sort();

        return Results.Ok(new Response(
            rawTimes[2],
            rollupTimes[2],
            rawRows,
            rollupRows,
            rawResult.SequenceEqual(rollupResult),
            rawResult.Length,
            start,
            end,
            Samples: 5,
            Statistic: "median after one warm-up; one repeatable-read snapshot",
            RowCountMeaning: "matching source rows, not physical rows scanned"));
    }

    public sealed record Response(
        double RawMs,
        double RollupMs,
        long RawRows,
        long RollupRows,
        bool Equivalent,
        int MatchingRows,
        DateTime WindowStart,
        DateTime WindowEnd,
        int Samples,
        string Statistic,
        string RowCountMeaning);
}
