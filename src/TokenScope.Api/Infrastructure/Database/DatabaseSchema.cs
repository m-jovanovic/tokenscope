using Npgsql;

namespace TokenScope.Api.Infrastructure.Database;

public static class DatabaseSchema
{
    private const int MaximumStartupAttempts = 15;

    public static async Task EnsureCreatedAsync(
        NpgsqlDataSource database,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql");
        var schemaSql = await File.ReadAllTextAsync(schemaPath, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = await database.OpenConnectionAsync(cancellationToken);
                await RequireTimescaleAsync(connection, cancellationToken);

                await using var command = new NpgsqlCommand(schemaSql, connection)
                {
                    CommandTimeout = 60,
                };
                await command.ExecuteNonQueryAsync(cancellationToken);
                logger.LogInformation("Schema is up to date");
                return;
            }
            catch (NpgsqlException exception) when (attempt < MaximumStartupAttempts && exception.IsTransient)
            {
                // A local database container may still be starting. Configuration errors are not retried.
                logger.LogWarning(
                    "Database is temporarily unavailable; retrying startup ({Attempt}/{MaximumAttempts})",
                    attempt,
                    MaximumStartupAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (PostgresException exception)
            {
                throw new InvalidOperationException(DescribeSchemaFailure(exception.SqlState));
            }
            catch (NpgsqlException)
            {
                throw new InvalidOperationException(
                    "Could not connect to the configured database. Check its availability, connection settings and TLS requirements.");
            }
        }
    }

    private static async Task RequireTimescaleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS(SELECT 1 FROM pg_extension WHERE extname='timescaledb')";
        await using var command = new NpgsqlCommand(sql, connection);

        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                "TimescaleDB is required. Ask the database administrator to enable the timescaledb extension in this database before starting TokenScope. Plain PostgreSQL is not supported.");
        }
    }

    // Never retain the original database exception: it can contain connection or data details.
    private static string DescribeSchemaFailure(string sqlState) => sqlState switch
    {
        "42501" => "Database permissions are insufficient. The application role needs CONNECT, schema CREATE/USAGE and ownership of TokenScope tables, aggregates and jobs for additive startup migrations.",
        "28P01" or "28000" => "Database authentication failed. Check the configured database credentials.",
        "3D000" => "The configured database does not exist. Create it and enable TimescaleDB before starting TokenScope.",
        _ => $"Database schema initialization failed (SQLSTATE {sqlState}). Use a compatible TimescaleDB database (tested with 2.30.1), with declarative hypertables, sparse bloom indexes and continuous aggregates enabled.",
    };
}
