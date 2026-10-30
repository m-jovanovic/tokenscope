-- One row per Claude Code API request (the claude_code.api_request OpenTelemetry event).
-- Idempotent: safe to run on every startup, locally or against Tiger Cloud.

CREATE TABLE IF NOT EXISTS api_requests (
    time                  timestamptz NOT NULL,
    source                text        NOT NULL,  -- live, claude-local, history, or demo (kept separate)
    developer             text        NOT NULL,
    session_id            text        NOT NULL,
    model                 text        NOT NULL,
    query_source          text,
    agent_name            text,
    skill_name            text,
    mcp_server            text,
    input_tokens          integer     NOT NULL,
    output_tokens         integer     NOT NULL,
    cache_read_tokens     integer     NOT NULL,
    cache_creation_tokens integer     NOT NULL,
    cost_usd_micros       bigint,               -- estimated microdollars; NULL means unpriced
    duration_ms           integer,
    event_key             text,
    cost_source           text        NOT NULL DEFAULT 'reported',
    is_synthetic          boolean     NOT NULL DEFAULT false
) WITH (
    tsdb.hypertable,
    tsdb.partition_column = 'time',
    tsdb.chunk_interval   = '1 day',
    tsdb.segmentby        = 'source, developer',
    tsdb.orderby          = 'time DESC',
    tsdb.sparse_index     = 'bloom(session_id)'
);

-- Additive migration from the original MVP. Existing events and aggregates stay intact.
ALTER TABLE api_requests ALTER COLUMN cost_usd_micros DROP NOT NULL;
ALTER TABLE api_requests ADD COLUMN IF NOT EXISTS event_key text;
ALTER TABLE api_requests ADD COLUMN IF NOT EXISTS cost_source text NOT NULL DEFAULT 'reported';
ALTER TABLE api_requests ADD COLUMN IF NOT EXISTS is_synthetic boolean NOT NULL DEFAULT false;
CREATE TABLE IF NOT EXISTS request_keys (
    source text NOT NULL,
    event_key text NOT NULL,
    PRIMARY KEY (source, event_key)
);

-- Per-minute rollup for the live panel.
CREATE MATERIALIZED VIEW IF NOT EXISTS spend_1m
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT time_bucket(INTERVAL '1 minute', time) AS bucket,
       source,
       developer,
       model,
       count(*)                              AS requests,
       sum(cost_usd_micros)::bigint          AS cost_usd_micros,
       sum(input_tokens)::bigint             AS input_tokens,
       sum(output_tokens)::bigint            AS output_tokens,
       sum(cache_read_tokens)::bigint        AS cache_read_tokens,
       sum(cache_creation_tokens)::bigint    AS cache_creation_tokens
FROM api_requests
GROUP BY bucket, source, developer, model
WITH NO DATA;

-- Hourly rollup with the full attribution (agent, skill, MCP server).
CREATE MATERIALIZED VIEW IF NOT EXISTS spend_1h
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT time_bucket(INTERVAL '1 hour', time) AS bucket,
       source,
       developer,
       model,
       query_source,
       agent_name,
       skill_name,
       mcp_server,
       count(*)                              AS requests,
       sum(cost_usd_micros)::bigint          AS cost_usd_micros,
       sum(input_tokens)::bigint             AS input_tokens,
       sum(output_tokens)::bigint            AS output_tokens,
       sum(cache_read_tokens)::bigint        AS cache_read_tokens,
       sum(cache_creation_tokens)::bigint    AS cache_creation_tokens
FROM api_requests
GROUP BY bucket, source, developer, model, query_source, agent_name, skill_name, mcp_server
WITH NO DATA;

-- Daily rollup built on top of the hourly one (hierarchical continuous aggregate).
CREATE MATERIALIZED VIEW IF NOT EXISTS spend_1d
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT time_bucket(INTERVAL '1 day', bucket) AS bucket,
       source,
       developer,
       model,
       query_source,
       agent_name,
       skill_name,
       mcp_server,
       sum(requests)::bigint                 AS requests,
       sum(cost_usd_micros)::bigint          AS cost_usd_micros,
       sum(input_tokens)::bigint             AS input_tokens,
       sum(output_tokens)::bigint            AS output_tokens,
       sum(cache_read_tokens)::bigint        AS cache_read_tokens,
       sum(cache_creation_tokens)::bigint    AS cache_creation_tokens
FROM spend_1h
GROUP BY 1, source, developer, model, query_source, agent_name, skill_name, mcp_server
WITH NO DATA;

SELECT add_continuous_aggregate_policy('spend_1m',
    start_offset => INTERVAL '2 hours', end_offset => INTERVAL '1 minute',
    schedule_interval => INTERVAL '1 minute', if_not_exists => true);

SELECT add_continuous_aggregate_policy('spend_1h',
    start_offset => INTERVAL '3 days', end_offset => INTERVAL '1 hour',
    schedule_interval => INTERVAL '5 minutes', if_not_exists => true);

SELECT add_continuous_aggregate_policy('spend_1d',
    start_offset => INTERVAL '7 days', end_offset => INTERVAL '1 day',
    schedule_interval => INTERVAL '1 hour', if_not_exists => true);

-- Optional Claude collector checkpoints only hashed file identities and read offsets.
CREATE TABLE IF NOT EXISTS local_usage_cursors (
    source text NOT NULL, path_hash text NOT NULL, state jsonb NOT NULL,
    PRIMARY KEY(source,path_hash)
);
CREATE TABLE IF NOT EXISTS local_request_state (
    source text NOT NULL, event_key text NOT NULL, payload jsonb NOT NULL,
    PRIMARY KEY(source,event_key)
);
CREATE TABLE IF NOT EXISTS local_file_requests (
    source text NOT NULL, path_hash text NOT NULL, event_key text NOT NULL,
    PRIMARY KEY(source,path_hash,event_key)
);
CREATE TABLE IF NOT EXISTS local_refresh_state (
    singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
    min_time timestamptz NOT NULL, revision bigint NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS ix_api_requests_source_event ON api_requests(source,event_key);
