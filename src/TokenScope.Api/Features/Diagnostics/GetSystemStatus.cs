using Dapper;
using Npgsql;
using TokenScope.Api.Features.Telemetry;
using TokenScope.Api.Infrastructure.Http;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.Diagnostics;

public static class GetSystemStatus
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        NpgsqlDataSource database,
        IngestState state,
        IngestQueue queue,
        CancellationToken cancellationToken)
    {
        var databaseStatus = new DatabaseStatus(false, null, null);
        IEnumerable<SourceStatus> sources = [];
        IEnumerable<AggregateStatus> aggregates = [];
        IEnumerable<JobStatus> jobs = [];
        var chunks = new ChunkStatus(0, 0, 0);

        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            var versions = await connection.QuerySingleAsync<DatabaseVersion>(new CommandDefinition("""
                SELECT current_setting('server_version') AS PostgresVersion,
                  (SELECT extversion FROM pg_extension WHERE extname='timescaledb') AS TimescaleVersion
                """, cancellationToken: cancellationToken));

            databaseStatus = new DatabaseStatus(true, versions.PostgresVersion, versions.TimescaleVersion);
            sources = await ReadSourcesAsync(connection, cancellationToken);
            aggregates = await connection.QueryAsync<AggregateStatus>(new CommandDefinition("""
                SELECT view_name AS name,materialized_only AS "materializedOnly"
                FROM timescaledb_information.continuous_aggregates
                WHERE hypertable_name='api_requests' OR view_name='spend_1d' ORDER BY view_name
                """, cancellationToken: cancellationToken));

            chunks = await connection.QuerySingleAsync<ChunkStatus>(new CommandDefinition("""
                SELECT count(*) AS total,count(*) FILTER (WHERE NOT is_compressed) AS rowstore,
                  count(*) FILTER (WHERE is_compressed) AS columnstore
                FROM timescaledb_information.chunks WHERE hypertable_name='api_requests'
                """, cancellationToken: cancellationToken));

            jobs = await connection.QueryAsync<JobStatus>(new CommandDefinition("""
                SELECT j.proc_name AS "procName",j.schedule_interval::text AS "scheduleInterval",
                  s.last_run_status AS "lastRunStatus"
                FROM timescaledb_information.jobs j
                LEFT JOIN timescaledb_information.job_stats s USING(job_id)
                WHERE j.proc_name IN ('policy_refresh_continuous_aggregate','policy_compression','policy_retention')
                ORDER BY j.job_id
                """, cancellationToken: cancellationToken));
        }
        catch (NpgsqlException)
        {
            databaseStatus = new DatabaseStatus(false, null, null);
        }

        return Results.Ok(new Response(
            databaseStatus,
            state.Snapshot(queue),
            sources,
            aggregates,
            chunks,
            jobs,
            new HistoryStatus(BoundedUpload.MaxBytes, ClaudePricing.Source, true),
            [
                "Costs are API-equivalent estimates or exported estimates, not subscription bills.",
                "Missing attribution stays unavailable. History import does not infer agent, skill or MCP labels.",
                "Ingestion uses an in-memory queue: acknowledged but unpersisted events can be lost if the process exits.",
                "No retention policy is installed; data is not automatically deleted. Day boundaries use UTC.",
                "History imports are first-write-wins per request. Import completed sessions; later revisions of an imported request are skipped.",
            ]));
    }

    public static Task<IEnumerable<SourceStatus>> ReadSourcesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        return connection.QueryAsync<SourceStatus>(new CommandDefinition("""
            SELECT source AS Source,count(*) AS Requests,count(cost_usd_micros) AS PricedRequests,
              count(*) FILTER (WHERE cost_usd_micros IS NULL) AS UnpricedRequests,
              max(time) AS LastEventAt,count(agent_name) AS AgentRequests,count(skill_name) AS SkillRequests,
              count(mcp_server) AS McpRequests,
              count(*) FILTER (WHERE is_synthetic OR source='demo') AS SyntheticRequests
            FROM api_requests WHERE source=ANY(@sources) GROUP BY source ORDER BY source
            """, new
        {
            sources = UsageSources.Active
        }, commandTimeout: 15, cancellationToken: cancellationToken));
    }

    public sealed record Response(
        DatabaseStatus Database,
        object Ingestion,
        IEnumerable<SourceStatus> Sources,
        IEnumerable<AggregateStatus> Aggregates,
        ChunkStatus Chunks,
        IEnumerable<JobStatus> Jobs,
        HistoryStatus History,
        string[] Limitations);

    public sealed record DatabaseStatus(bool Connected, string? PostgresVersion, string? TimescaleVersion);

    public sealed record SourceStatus(
        string Source,
        long Requests,
        long PricedRequests,
        long UnpricedRequests,
        DateTime? LastEventAt,
        long AgentRequests,
        long SkillRequests,
        long McpRequests,
        long SyntheticRequests);

    public sealed record AggregateStatus(string Name, bool MaterializedOnly);

    public sealed record ChunkStatus(long Total, long Rowstore, long Columnstore);

    public sealed record JobStatus(string ProcName, string ScheduleInterval, string? LastRunStatus);

    public sealed record HistoryStatus(long MaxBytes, string PriceSource, bool PreviewRequired);

    private sealed record DatabaseVersion(string PostgresVersion, string? TimescaleVersion);
}
