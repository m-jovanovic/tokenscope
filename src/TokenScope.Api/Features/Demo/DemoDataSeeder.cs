using Npgsql;
using TokenScope.Api.Infrastructure.Database;

namespace TokenScope.Api.Features.Demo;

public static class DemoDataSeeder
{
    public static async Task<long> SeedAsync(
        NpgsqlDataSource database,
        int developers,
        int days,
        CancellationToken cancellationToken)
    {
        if (developers is < 1 or > 25 || days is < 1 or > 90)
        {
            throw new InvalidDataException("Developers must be 1–25 and days must be 1–90.");
        }

        var seedPath = Path.Combine(AppContext.BaseDirectory, "Database", "seed-demo.sql");
        var seedSql = await File.ReadAllTextAsync(seedPath, cancellationToken);
        long inserted;

        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            const string deleteSql = """
                DELETE FROM api_requests WHERE source='demo';
                DELETE FROM request_keys WHERE source='demo'
                """;
            await using (var delete = new NpgsqlCommand(deleteSql, connection, transaction))
            {
                delete.CommandTimeout = 300;
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insert = new NpgsqlCommand(seedSql, connection, transaction))
            {
                insert.CommandTimeout = 300;
                insert.Parameters.Add(new NpgsqlParameter { Value = developers });
                insert.Parameters.Add(new NpgsqlParameter { Value = days });
                inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await ContinuousAggregateRefresher.RefreshDemoAsync(database, cancellationToken);
        return inserted;
    }
}
