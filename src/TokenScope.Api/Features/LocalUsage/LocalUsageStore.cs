using System.Text.Json;
using Dapper;
using Npgsql;
using TokenScope.Api.Infrastructure.Database;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.LocalUsage;

public sealed class LocalUsageStore(NpgsqlDataSource dataSource)
{
    public sealed record SaveResult(LocalCursor Cursor, int Inserted, int Updated, int Skipped);

    public async Task<Dictionary<string, LocalCursor>> LoadCursorsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var states = await connection.QueryAsync<CursorState>(new CommandDefinition(
            """
            SELECT source AS Source, path_hash AS PathHash, state::text AS State
            FROM local_usage_cursors WHERE source = @source
            """,
            new
            {
                source = UsageSources.ClaudeLocal
            }, cancellationToken: cancellationToken));

        return states
            .Select(state => ReadCursor(state.Source, state.PathHash, state.State))
            .ToDictionary(cursor => cursor.Source + ":" + cursor.PathHash);
    }

    public static LocalCursor ReadCursor(string source, string pathHash, string state)
    {
        if (source != UsageSources.ClaudeLocal)
        {
            throw new InvalidDataException("Unsupported local usage source.");
        }

        try
        {
            var cursor = JsonSerializer.Deserialize<LocalCursor>(state);
            if (cursor is not null && IsValidCursor(cursor, source, pathHash))
            {
                return cursor;
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or OverflowException)
        {
            // A damaged checkpoint can be rebuilt by replaying the file against the usage ledger.
        }

        return new LocalCursor { Source = source, PathHash = pathHash };
    }

    private static bool IsValidCursor(LocalCursor cursor, string source, string pathHash)
    {
        return cursor.Source == source && cursor.PathHash == pathHash &&
               cursor.Offset >= 0 && cursor.Length >= 0 && cursor.PrefixLength is >= 0 and <= 4_096 &&
               cursor.Inserted >= 0 && cursor.Updated >= 0 && cursor.SkippedRecords >= 0 &&
               cursor.PrefixHash is not null && cursor.AnchorHash is not null;
    }

    public static ApiRequestRow Merge(ApiRequestRow prior, ApiRequestRow next)
    {
        return next with
        {
            Time = ToMicroseconds(prior.Time < next.Time ? prior.Time : next.Time),
            InputTokens = Math.Max(prior.InputTokens, next.InputTokens),
            OutputTokens = Math.Max(prior.OutputTokens, next.OutputTokens),
            CacheReadTokens = Math.Max(prior.CacheReadTokens, next.CacheReadTokens),
            CacheCreationTokens = Math.Max(prior.CacheCreationTokens, next.CacheCreationTokens),
            Model = next.Model == "unknown" ? prior.Model : next.Model,
            CostUsdMicros = MaximumKnownCost(prior.CostUsdMicros, next.CostUsdMicros),
            CostSource = next.CostUsdMicros is null && prior.CostUsdMicros is not null
                ? prior.CostSource
                : next.CostSource,
        };
    }

    public async Task<SaveResult> SaveAsync(
        LocalCursor cursor,
        IReadOnlyList<ApiRequestRow> rows,
        int skipped,
        CancellationToken cancellationToken)
    {
        ValidateSourceAndIdentity(cursor, rows);
        var unique = NormalizeAndMergeBatch(rows);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockSourceAsync(connection, transaction, cursor.Source, cancellationToken);
        var existing = await LoadRequestStatesAsync(
            connection, transaction, cursor.Source, unique, cancellationToken);
        var changes = FindChanges(unique, existing);

        if (changes.Rows.Count > 0)
        {
            await ReplaceUsageRowsAsync(
                connection, transaction, cursor.Source, changes.Rows, existing, cancellationToken);
            await SaveRequestStatesAsync(connection, transaction, changes.Rows, cancellationToken);
            await MarkRefreshPendingAsync(
                connection, transaction, changes.Rows.Min(row => row.Time), cancellationToken);
        }

        if (unique.Length > 0)
        {
            await SaveFileRequestKeysAsync(connection, transaction, cursor, unique, cancellationToken);
        }

        var totalSkipped = skipped + rows.Count - changes.Rows.Count;
        var updatedCursor = cursor with
        {
            Inserted = cursor.Inserted + changes.Inserted,
            Updated = cursor.Updated + changes.Updated,
            SkippedRecords = cursor.SkippedRecords + totalSkipped,
            LastPersistedAt = changes.Inserted + changes.Updated > 0 ? DateTime.UtcNow : cursor.LastPersistedAt,
        };
        await SaveCursorAsync(connection, transaction, updatedCursor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SaveResult(updatedCursor, changes.Inserted, changes.Updated, totalSkipped);
    }

    private static void ValidateSourceAndIdentity(LocalCursor cursor, IReadOnlyList<ApiRequestRow> rows)
    {
        if (cursor.Source != UsageSources.ClaudeLocal ||
            rows.Any(row => row.Source != cursor.Source || row.EventKey is null))
        {
            throw new InvalidDataException("Invalid local usage source or identity.");
        }
    }

    private static ApiRequestRow[] NormalizeAndMergeBatch(IReadOnlyList<ApiRequestRow> rows)
    {
        // JSON timestamptz parsing rounds 100ns digits; Npgsql truncates them. Normalize before either path.
        return rows
            .Select(row => row with { Time = ToMicroseconds(row.Time) })
            .GroupBy(row => row.EventKey!)
            .Select(group => group.Aggregate(Merge))
            .ToArray();
    }

    private static async Task LockSourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string source,
        CancellationToken cancellationToken)
    {
        // Row locks cannot protect a new key. Serialize writers for this source across app processes.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext('tokenscope-local-usage'), hashtext(@source))",
            new
            {
                source
            }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task<Dictionary<string, ApiRequestRow>> LoadRequestStatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string source,
        ApiRequestRow[] rows,
        CancellationToken cancellationToken)
    {
        var saved = await connection.QueryAsync<SavedRow>(new CommandDefinition(
            """
            SELECT event_key AS EventKey, payload::text AS Payload
            FROM local_request_state
            WHERE source = @source AND event_key = ANY(@keys)
            FOR UPDATE
            """,
            new
            {
                source,
                keys = rows.Select(row => row.EventKey!).ToArray()
            },
            transaction, cancellationToken: cancellationToken));

        return saved.ToDictionary(
            row => row.EventKey, row => JsonSerializer.Deserialize<ApiRequestRow>(row.Payload)!);
    }

    private static ChangeSet FindChanges(
        ApiRequestRow[] candidates,
        Dictionary<string, ApiRequestRow> existing)
    {
        var changed = new List<ApiRequestRow>();
        var inserted = 0;
        var updated = 0;

        foreach (var candidate in candidates)
        {
            if (existing.TryGetValue(candidate.EventKey!, out var prior))
            {
                var merged = Merge(prior, candidate);
                if (merged == prior)
                {
                    continue;
                }

                changed.Add(merged);
                updated++;
            }
            else
            {
                changed.Add(candidate);
                inserted++;
            }
        }

        return new ChangeSet(changed, inserted, updated);
    }

    private static async Task ReplaceUsageRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string source,
        List<ApiRequestRow> changed,
        Dictionary<string, ApiRequestRow> existing,
        CancellationToken cancellationToken)
    {
        var priorRows = changed
            .Where(row => existing.ContainsKey(row.EventKey!))
            .Select(row => existing[row.EventKey!])
            .ToArray();
        if (priorRows.Length > 0)
        {
            // Earlier JSON inserts could round up a 100ns digit. Include a microsecond on either side,
            // while limiting deletion to the exact source and keys being replaced.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM api_requests
                WHERE source = @source AND event_key = ANY(@keys) AND time >= @start AND time <= @end
                """,
                new
                {
                    source,
                    keys = priorRows.Select(row => row.EventKey!).ToArray(),
                    start = ToMicroseconds(priorRows.Min(row => row.Time)).AddTicks(-10),
                    end = ToMicroseconds(priorRows.Max(row => row.Time)).AddTicks(10),
                },
                transaction, cancellationToken: cancellationToken));
        }

        var payload = SerializeUsageRows(changed);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO api_requests (
                time, source, developer, session_id, model, query_source, agent_name, skill_name, mcp_server,
                input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                cost_usd_micros, duration_ms, event_key, cost_source, is_synthetic)
            SELECT * FROM jsonb_to_recordset(@payload::jsonb) AS r (
                time timestamptz, source text, developer text, session_id text, model text,
                query_source text, agent_name text, skill_name text, mcp_server text,
                input_tokens integer, output_tokens integer, cache_read_tokens integer,
                cache_creation_tokens integer, cost_usd_micros bigint, duration_ms integer,
                event_key text, cost_source text, is_synthetic boolean)
            """,
            new
            {
                payload
            }, transaction, cancellationToken: cancellationToken));
    }

    private static string SerializeUsageRows(List<ApiRequestRow> rows)
    {
        return JsonSerializer.Serialize(rows.Select(row => new
        {
            time = row.Time,
            source = row.Source,
            developer = row.Developer,
            session_id = row.SessionId,
            model = row.Model,
            query_source = row.QuerySource,
            agent_name = row.AgentName,
            skill_name = row.SkillName,
            mcp_server = row.McpServer,
            input_tokens = row.InputTokens,
            output_tokens = row.OutputTokens,
            cache_read_tokens = row.CacheReadTokens,
            cache_creation_tokens = row.CacheCreationTokens,
            cost_usd_micros = row.CostUsdMicros,
            duration_ms = row.DurationMs,
            event_key = row.EventKey,
            cost_source = row.CostSource,
            is_synthetic = row.IsSynthetic,
        }));
    }

    private static async Task SaveRequestStatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<ApiRequestRow> rows,
        CancellationToken cancellationToken)
    {
        var states = JsonSerializer.Serialize(rows.Select(row => new
        {
            source = row.Source,
            event_key = row.EventKey,
            payload = row,
        }));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO local_request_state (source, event_key, payload)
            SELECT source, event_key, payload
            FROM jsonb_to_recordset(@states::jsonb) AS r (source text, event_key text, payload jsonb)
            ON CONFLICT (source, event_key) DO UPDATE SET payload = EXCLUDED.payload
            """,
            new
            {
                states
            }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task MarkRefreshPendingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTime earliestChangedTime,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO local_refresh_state (singleton, min_time) VALUES (true, @start)
            ON CONFLICT (singleton) DO UPDATE
            SET min_time = least(local_refresh_state.min_time, EXCLUDED.min_time),
                revision = local_refresh_state.revision + 1
            """,
            new
            {
                start = earliestChangedTime
            }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task SaveFileRequestKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LocalCursor cursor,
        ApiRequestRow[] rows,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO local_file_requests (source, path_hash, event_key)
            SELECT @source, @pathHash, unnest(@keys::text[])
            ON CONFLICT DO NOTHING
            """,
            new
            {
                source = cursor.Source,
                pathHash = cursor.PathHash,
                keys = rows.Select(row => row.EventKey!).ToArray(),
            },
            transaction, cancellationToken: cancellationToken));
    }

    private static async Task SaveCursorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LocalCursor cursor,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO local_usage_cursors (source, path_hash, state) VALUES (@source, @pathHash, @state::jsonb)
            ON CONFLICT (source, path_hash) DO UPDATE SET state = EXCLUDED.state
            """,
            new
            {
                source = cursor.Source,
                pathHash = cursor.PathHash,
                state = JsonSerializer.Serialize(cursor)
            },
            transaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> HasPendingRefreshAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM local_refresh_state)", cancellationToken: cancellationToken));
    }

    public async Task RefreshPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var pending = await connection.QuerySingleOrDefaultAsync<PendingRefresh>(new CommandDefinition(
            "SELECT min_time AS MinTime, revision AS Revision FROM local_refresh_state",
            cancellationToken: cancellationToken));
        if (pending is null)
        {
            return;
        }

        await ContinuousAggregateRefresher.RefreshAsync(dataSource, pending.MinTime, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM local_refresh_state WHERE revision = @revision",
            new
            {
                revision = pending.Revision
            }, cancellationToken: cancellationToken));
    }

    private static DateTime ToMicroseconds(DateTime value)
    {
        return new DateTime(value.Ticks - value.Ticks % 10, value.Kind);
    }

    private static long? MaximumKnownCost(long? prior, long? next)
    {
        if (prior is null)
        {
            return next;
        }

        return next is null ? prior : Math.Max(prior.Value, next.Value);
    }

    private sealed record ChangeSet(List<ApiRequestRow> Rows, int Inserted, int Updated);
    private sealed record SavedRow(string EventKey, string Payload);
    private sealed record CursorState(string Source, string PathHash, string State);
    private sealed record PendingRefresh(DateTime MinTime, long Revision);
}
