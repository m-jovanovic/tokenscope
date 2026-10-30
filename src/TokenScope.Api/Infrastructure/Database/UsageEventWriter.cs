using Dapper;
using Npgsql;
using NpgsqlTypes;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Infrastructure.Database;

public sealed class UsageEventWriter(NpgsqlDataSource database)
{
    public async Task<int> InsertAsync(
        IReadOnlyCollection<ApiRequestRow> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return 0;
        }

        var uniqueRequests = requests
            .GroupBy(request => (request.Source, EventKey: UsageIdentity.Key(request)))
            .Select(group => (Request: group.First(), group.Key.EventKey))
            .ToArray();

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The ordinary key table enforces uniqueness across time chunks. Claims and COPY
        // share a transaction so a failed write cannot leave a request permanently claimed.
        const string claimSql = """
            INSERT INTO request_keys(source, event_key)
            SELECT * FROM unnest(@sources::text[], @keys::text[])
            ON CONFLICT DO NOTHING
            RETURNING source || ':' || event_key
            """;

        var claimedKeys = (await connection.QueryAsync<string>(new CommandDefinition(
            claimSql,
            new
            {
                sources = uniqueRequests.Select(item => item.Request.Source).ToArray(),
                keys = uniqueRequests.Select(item => item.EventKey).ToArray(),
            },
            transaction,
            cancellationToken: cancellationToken))).ToHashSet();

        var acceptedRequests = uniqueRequests
            .Where(item => claimedKeys.Contains(item.Request.Source + ":" + item.EventKey))
            .ToArray();

        if (acceptedRequests.Length > 0)
        {
            const string copySql = """
                COPY api_requests (time, source, developer, session_id, model, query_source, agent_name, skill_name,
                  mcp_server, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                  cost_usd_micros, duration_ms, event_key, cost_source, is_synthetic) FROM STDIN (FORMAT BINARY)
                """;

            await using var writer = await connection.BeginBinaryImportAsync(copySql, cancellationToken);
            foreach (var (request, eventKey) in acceptedRequests)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(request.Time, NpgsqlDbType.TimestampTz, cancellationToken);
                await WriteTextAsync(writer, request.Source, cancellationToken);
                await WriteTextAsync(writer, request.Developer, cancellationToken);
                await WriteTextAsync(writer, request.SessionId, cancellationToken);
                await WriteTextAsync(writer, request.Model, cancellationToken);
                await WriteTextAsync(writer, request.QuerySource, cancellationToken);
                await WriteTextAsync(writer, request.AgentName, cancellationToken);
                await WriteTextAsync(writer, request.SkillName, cancellationToken);
                await WriteTextAsync(writer, request.McpServer, cancellationToken);
                await writer.WriteAsync(request.InputTokens, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(request.OutputTokens, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(request.CacheReadTokens, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(request.CacheCreationTokens, NpgsqlDbType.Integer, cancellationToken);

                if (request.CostUsdMicros is { } cost)
                {
                    await writer.WriteAsync(cost, NpgsqlDbType.Bigint, cancellationToken);
                }
                else
                {
                    await writer.WriteNullAsync(cancellationToken);
                }

                if (request.DurationMs is { } duration)
                {
                    await writer.WriteAsync(duration, NpgsqlDbType.Integer, cancellationToken);
                }
                else
                {
                    await writer.WriteNullAsync(cancellationToken);
                }

                await WriteTextAsync(writer, eventKey, cancellationToken);
                await WriteTextAsync(writer, request.CostSource, cancellationToken);
                await writer.WriteAsync(request.IsSynthetic, NpgsqlDbType.Boolean, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return acceptedRequests.Length;
    }

    private static Task WriteTextAsync(
        NpgsqlBinaryImporter writer,
        string? value,
        CancellationToken cancellationToken) =>
        value is null
            ? writer.WriteNullAsync(cancellationToken)
            : writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
}
