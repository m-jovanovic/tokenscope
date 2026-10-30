using Npgsql;

namespace TokenScope.Api.Infrastructure.Database;

public static class DatabaseConfiguration
{
    public static string ConnectionString(string? configured, string? fallback = null)
    {
        var settingName = "ConnectionStrings__tokenscope";
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = fallback;
            settingName = "ConnectionStrings__Database";
        }
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("Set ConnectionStrings__tokenscope or ConnectionStrings__Database to a PostgreSQL connection string for a database with the TimescaleDB extension enabled.");
        }

        try
        {
            var parsed = new NpgsqlConnectionStringBuilder(configured);
            if (string.IsNullOrWhiteSpace(parsed.Host) || string.IsNullOrWhiteSpace(parsed.Database))
            {
                throw new ArgumentException();
            }

            parsed.Timezone = "UTC";
            parsed.IncludeErrorDetail = false;
            return parsed.ConnectionString;
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{settingName} is invalid. Supply an Npgsql connection string with Host and Database; credentials are never logged.");
        }
    }
}
