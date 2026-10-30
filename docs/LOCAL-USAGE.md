# Collect local Claude Code history

The optional collector backfills supported Claude Code session files and follows appended usage while the app runs. It writes to **Claude history** (`claude-local`). Live OpenTelemetry events use the separate `live` source.

## Bundled local setup

With Docker running and PowerShell 7 available, run from the repository root:

```powershell
.\scripts\start.ps1 -LocalUsage
```

The helper finds the Claude projects folder, writes an ignored Compose override with a read-only mount, and starts the bundled app and local database. It builds the image unless you pass `-NoBuild`. The .NET background worker performs the import after startup.

Open [localhost:5299](http://localhost:5299) and select **Claude history**. **Data & setup** shows progress and provides **Sync now**. The first scan imports supported history; subsequent scans check for appended records every five seconds. Allow for Claude Code's own delay in writing usage and the dashboard's polling interval.

The helper remembers collection in `.artifacts`. Later runs of `scripts/start.ps1` preserve that preference. Plain `docker compose up` does not apply the generated folder override. To disable collection while retaining imported data:

```powershell
.\scripts\start.ps1 -DisableLocalUsage -NoBuild
```

Use `docker compose stop` to stop the containers. The next enabled startup catches up with records written while the app was stopped.

## Existing or remote-database deployments

`start.ps1 -LocalUsage` selects the bundled local Compose configuration. For an existing deployment, add the read-only mount and collector settings to that deployment instead; preserve its database connection.

The default host folder is `%USERPROFILE%\.claude\projects` on Windows or `~/.claude/projects` on Unix-like systems. Set `TOKENSCOPE_CLAUDE_PROJECTS` before running the helper to use another projects folder. The helper mounts the projects folder, not Claude's configuration or authentication files.

For a separately built container, this Linux-shell example uses a private `.env` with the database connection:

```sh
docker run --rm --name tokenscope \
  --env-file .env \
  -p 127.0.0.1:5299:8080 \
  --mount "type=bind,source=$HOME/.claude/projects,target=/usage/claude,readonly" \
  -e LocalUsage__Enabled=true \
  -e LocalUsage__ClaudePath=/usage/claude \
  tokenscope:local
```

The container's non-root user needs read and directory traversal access to the mount. For a native API process, set the same environment variables with a host path. Metadata is written to the configured database, including a remote database when one is configured.

## Counting and privacy

A response can have several usage snapshots. The collector groups supported records by session/request identity and merges growing token totals. Repeated scans or copied files with the same identity do not add another request. When request identity is absent, supported records can use message identity.

Durable file offsets support restart catch-up. Incomplete trailing JSONL lines wait for completion; truncated or replaced files are rescanned with replay protection. Historical aggregates are refreshed after collection. **Pending refresh** means those aggregates are still catching up.

Skipped records can include non-usage messages, unsupported shapes, and records without usable identity. A session file contains more than API requests, so its line count is not an expected request count. Missing attribution remains unavailable.

The collector stores usage fields, hashed request/session identifiers, and hashed file checkpoints. It excludes prompts, responses, tool inputs, raw records, project names, and local paths from persisted collector data. Source files are never edited. Host paths remain in the ignored local Compose override.

## Sources and cost estimates

`claude-local`, `live`, and manually uploaded `history` can describe the same requests. There is no cross-source deduplication; compare each source separately. Generated sample data uses `demo` and is labelled synthetic.

Manual JSONL uploads are intended for completed sessions. They require a preview, accept at most 10 MiB / 50,000 records, and keep the first imported version of a request across uploads. Previews expire after ten minutes. Use the folder collector to follow growing files.

Reported costs take precedence when present. Otherwise, supported models use the dated rate card in [ClaudePricing.cs](../src/TokenScope.Api/Shared/Usage/ClaudePricing.cs), currently labelled `2026-10-21`. The app does not download price updates or reconstruct historical invoices. It assumes standard API processing where the parser permits it.

Unknown models, unsupported pricing modifiers, and ambiguous cache-write duration stay unpriced. Dollar totals cover priced requests only; token and request counts can include unpriced records. Claude's session format can change, so inspect date coverage and pricing coverage for your own files.

## Verify collection

Select **Claude history** and a range including today. Note the last-event time and count, complete a Claude Code turn, then wait for the file write and collector scan. This verifies collection separately from the OpenTelemetry exporter.

For deterministic checks without personal logs or model requests:

```powershell
.\scripts\verify-local-usage.ps1
```

The script uses generated fixtures and disposable resources to check backfill, appends, growing snapshots, incomplete lines, replay, restart recovery, pricing, and aggregate equality. Fixture results do not prove complete coverage of every Claude Code version.
