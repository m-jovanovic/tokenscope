# Architecture

The React client calls an ASP.NET Core API. Npgsql provides PostgreSQL connections and binary COPY; Dapper maps parameterized query results. TimescaleDB stores request events and maintains rollups for dashboard queries.

```text
Claude Code OTLP → bounded queue → batch writer → api_requests
Local JSONL     → collector and checkpoints     → api_requests
Manual JSONL    → preview and import            → api_requests
                                                    ↓
                                      minute / hour / day rollups
                                                    ↓
                                           dashboard and MCP
```

Each source has separate totals. Inputs can overlap, so the application does not combine them into a single personal total.

## Storage and queries

`api_requests` is a hypertable partitioned by event time into daily chunks. Queries use the same SQL table name while TimescaleDB manages the chunks. Costs use integer microdollars; `NULL` means unpriced.

The ordinary `request_keys` table has a composite primary key on `(source, event_key)`. `UsageEventWriter` claims replay keys and writes accepted rows with binary COPY in one transaction. This keeps request identity unique within a source even when an event's timestamp changes. The hypertable's source/event index supports lookup; it is not the replay constraint.

`spend_1m` and `spend_1h` are continuous aggregates over raw events. `spend_1d` rolls up hourly totals. Refresh policies maintain completed windows, and import paths explicitly refresh historical ranges. Query code combines aggregate results for complete buckets with raw rows at partial boundaries. This preserves exact requested time windows while reducing repeated aggregation of older rows.

The local collector has a separate store because growing snapshots require merges, checkpoints, and replacement of earlier usage. Manual imports use first-write-wins replay protection. Keep those behaviors explicit when changing ingestion.

## Code organization

```text
src/TokenScope.Api/
  Program.cs                  Configuration and feature registration
  Features/
    Analytics/                Dashboard queries, benchmark, MCP
    Demo/                     Labelled sample data
    Diagnostics/              Health, database, ingestion status
    History/                  Preview and import completed JSONL files
    LocalUsage/               Follow local session files
    Telemetry/                Receive OTLP and queue batches
  Infrastructure/
    Database/                 Schema, batch writes, aggregate refreshes
    Http/                     Upload limits, errors, origin checks
  Shared/
    Time/                     UTC bucket boundaries
    Usage/                    Records, identity, tokens, pricing
```

The API follows vertical slices: each use case keeps its endpoint, SQL, and request/response types together. Start with [GetTimeSeries.cs](../src/TokenScope.Api/Features/Analytics/GetTimeSeries.cs) or [ImportHistory.cs](../src/TokenScope.Api/Features/History/ImportHistory.cs).

Handlers use pooled connections, parameterized values, cancellation tokens, and asynchronous disposal. Dynamic SQL identifiers come from fixed allowlists. There is no mediator or generic repository between a handler and its SQL. Share behavior when another slice needs the same operation.

## Boundaries

The live queue is bounded and held in memory. Database retries do not make acceptance durable before persistence. Local collector checkpoints are durable database state and must be backed up with the usage rows.

Parsers retain allowlisted usage fields. Unknown prices and missing attribution stay unknown. The pricing rules are versioned in [ClaudePricing.cs](../src/TokenScope.Api/Shared/Usage/ClaudePricing.cs).

MCP reports use fixed SQL in read-only transactions with five-second statement timeouts. The deprecated `run_readonly_sql` tool always rejects SQL and directs callers to fixed reports. These restrictions apply only to TokenScope's MCP operations. The app has no built-in authentication; see [deployment](DEPLOYMENT.md).

See [contributing](../CONTRIBUTING.md) for formatting, builds, and isolated verification.
