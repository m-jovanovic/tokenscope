-- Generates a simulated team's Claude Code usage (source = 'demo').
-- Parameters: $1 = number of developers (max 25), $2 = number of days.
-- Prices are USD per million tokens and only feed the demo numbers.
-- A runaway 'test-fixer' subagent is planted for developer 'chen', 12 days ago.

WITH names AS (
    SELECT ARRAY['ana','ben','chen','dara','eli','fatima','gus','hana','ivan','jo','kai','lena','marco',
                 'nina','omar','priya','quinn','rosa','sam','tariq','uma','vik','wen','yusuf','zoe'] AS n
),
devs AS (
    SELECT d AS idx, (SELECT n[d] FROM names) AS developer, 0.35 + random() * 1.3 AS intensity
    FROM generate_series(1, least($1, 25)) AS d
),
days AS (
    SELECT day::date AS day
    FROM generate_series(current_date - ($2 - 1), current_date, INTERVAL '1 day') AS day
),
dev_days AS (
    SELECT developer, day,
           (CASE WHEN extract(isodow FROM day) >= 6 THEN 0.12 ELSE 1.0 END
              * intensity * (350 + random() * 500))::int AS n
    FROM devs CROSS JOIN days
),
reqs AS (
    SELECT developer, day, g AS seq,
           day + INTERVAL '8 hours' + random() * INTERVAL '10 hours' AS ts,
           random() AS r_model, random() AS r_agent, random() AS r_skill, random() AS r_mcp
    FROM dev_days, generate_series(1, n) AS g
),
shaped AS (
    SELECT ts, developer,
           'demo-' || developer || '-' || to_char(day, 'YYYYMMDD') || '-' || (seq / 70) AS session_id,
           CASE WHEN r_model < 0.55 THEN 'claude-opus-5-5'
                WHEN r_model < 0.88 THEN 'claude-sonnet-5'
                ELSE 'claude-haiku-4-5' END AS model,
           CASE WHEN r_agent < 0.70 THEN NULL
                WHEN r_agent < 0.83 THEN 'Explore'
                WHEN r_agent < 0.93 THEN 'general-purpose'
                ELSE 'code-reviewer' END AS agent_name,
           CASE WHEN r_skill < 0.86 THEN NULL
                WHEN r_skill < 0.92 THEN 'review'
                WHEN r_skill < 0.97 THEN 'simplify'
                ELSE 'frontend-design' END AS skill_name,
           CASE WHEN r_mcp < 0.88 THEN NULL
                WHEN r_mcp < 0.95 THEN 'github'
                ELSE 'playwright' END AS mcp_server,
           (200 + random() * 4000)::int     AS input_tokens,
           (80 + random() * 2200)::int      AS output_tokens,
           (8000 + random() * 70000)::int   AS cache_read_tokens,
           (random() * 6000)::int           AS cache_creation_tokens,
           (700 + random() * 18000)::int    AS duration_ms
    FROM reqs
    WHERE ts <= now()
),
incident AS (
    -- Planted: a test-fixer subagent stuck in a loop for three hours.
    SELECT (current_date - 12) + INTERVAL '14 hours' + random() * INTERVAL '3 hours' AS ts,
           'chen' AS developer,
           'demo-chen-incident' AS session_id,
           'claude-opus-5-5' AS model,
           'test-fixer' AS agent_name,
           NULL::text AS skill_name,
           NULL::text AS mcp_server,
           (1500 + random() * 6000)::int    AS input_tokens,
           (1500 + random() * 4000)::int    AS output_tokens,
           (60000 + random() * 90000)::int  AS cache_read_tokens,
           (2000 + random() * 12000)::int   AS cache_creation_tokens,
           (5000 + random() * 25000)::int   AS duration_ms
    FROM generate_series(1, 1400)
    WHERE $1 >= 3 AND $2 > 12
),
all_rows AS (
    SELECT * FROM shaped
    UNION ALL
    SELECT * FROM incident
),
priced AS (
    SELECT r.*,
           CASE r.model WHEN 'claude-opus-5-5' THEN 4.0 WHEN 'claude-sonnet-5' THEN 2.0 ELSE 1.0 END AS in_price,
           CASE r.model WHEN 'claude-opus-5-5' THEN 20.0 WHEN 'claude-sonnet-5' THEN 10.0 ELSE 5.0 END AS out_price
    FROM all_rows r
)
INSERT INTO api_requests (time, source, developer, session_id, model, query_source, agent_name, skill_name,
                          mcp_server, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                           cost_usd_micros, duration_ms, cost_source)
SELECT ts, 'demo', developer, session_id, model,
       coalesce(agent_name, 'repl_main_thread'),
       agent_name, skill_name, mcp_server,
       input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
       round(input_tokens * in_price
             + cache_read_tokens * in_price * 0.1
             + cache_creation_tokens * in_price * 2.0   -- Claude Code writes 1-hour cache entries (2x input price)
             + output_tokens * out_price)::bigint,
        duration_ms, 'synthetic'
FROM priced
ORDER BY ts;
