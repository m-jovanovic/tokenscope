# TokenScope

Track Claude Code requests, tokens, and estimated API costs in a .NET 10 + React dashboard backed by TimescaleDB. Import local history, follow new requests, and query usage through read-only MCP tools. The API uses Npgsql and Dapper; the container serves both the API and compiled client.

Costs are estimates based on API pricing. Unknown costs stay unpriced, and separate data sources can describe overlapping activity. TokenScope has no built-in authentication; keep it on loopback or protect all endpoints with an authenticated gateway.

## Quick start

Install Docker with Linux container support, then run:

```sh
git clone https://github.com/m-jovanovic/tokenscope.git
cd tokenscope
docker compose up --build -d
```

Open [localhost:5299](http://localhost:5299). Compose starts the app and an empty TimescaleDB database. The app and database ports bind to loopback at `5299` and `55432`. Set `TOKENSCOPE_PORT` or `TOKENSCOPE_DB_PORT` before startup if those ports are occupied. The bundled database credentials are for local development.

Use `docker compose stop` to stop the containers while preserving the database volume.

## Collect Claude Code usage

For live telemetry, start Claude Code from the repository root:

```sh
claude --settings ./claude-telemetry.json
```

The settings send OTLP/HTTP JSON request events to `http://localhost:5299/v1/logs`. Select **Live telemetry** in the dashboard, complete a Claude turn, and allow for export and dashboard polling. Update the endpoint in the settings file if you change the app port.

For local history and continued file collection, run this helper with PowerShell 7:

```powershell
.\scripts\start.ps1 -LocalUsage
```

The helper starts the bundled Compose setup and mounts your Claude projects folder read-only. The app imports supported history, follows appended records, and stores them under **Claude history**. For a deployment using your own database, add collection to that deployment as described in [local collection](docs/LOCAL-USAGE.md).

You can also preview and upload a completed Claude JSONL session through **Data & setup → Manual import**. Manual uploads keep the first imported version of a request; the folder collector can update growing usage snapshots.

## Use your own database

The database must have the TimescaleDB extension and support the features in [schema.sql](src/TokenScope.Api/Infrastructure/Database/schema.sql). Self-hosted TimescaleDB and Tiger Cloud use the same application image. Startup creates and updates the schema, so the connection role needs the required ownership and creation permissions.

Create an uncommitted `.env` file containing an Npgsql connection string:

```dotenv
ConnectionStrings__tokenscope=Host=<database-host>;Port=5432;Database=<database>;Username=<user>;Password=<secret>;SSL Mode=VerifyFull
```

`ConnectionStrings__Database` is supported as a fallback. A nonblank `ConnectionStrings__tokenscope` takes precedence; an invalid primary value fails startup instead of falling back.

```sh
docker build -t tokenscope:local .
docker run --rm --name tokenscope --env-file .env -p 127.0.0.1:5299:8080 tokenscope:local
```

The database hostname must be reachable from the container. See [deployment](docs/DEPLOYMENT.md) for TLS, permissions, networking, and backups.

No GHCR image has been published yet. Build locally until the first release. Released images will use `ghcr.io/m-jovanovic/tokenscope:<version>`; check the release before relying on a tag.

## Query with Claude Code

```sh
claude mcp add --transport http tokenscope http://localhost:5299/mcp
```

For example: “Using TokenScope, show daily estimated spend for the live source over the last seven days. Include the time range and pricing coverage.”

The reporting tools are `get_spend_breakdown`, `get_daily_spend`, `find_spend_spikes`, and `run_report`. They use fixed SQL, read-only transactions, and timeouts. Arbitrary SQL is rejected. Each response includes its source, time bounds, and priced/unpriced coverage.

## Data and operating limits

Choose **Light**, **Dark**, or **System** under Appearance. The dashboard shows request counts, tokens, estimated costs, model breakdowns, and available attribution. **Database** exposes chunks, aggregates, and a measured raw-versus-rollup comparison. Optional sample data is labelled synthetic and stays in its own source.

| Source | Input |
| --- | --- |
| `live` | Claude Code OpenTelemetry request events |
| `claude-local` | Local history backfill and continued file collection |
| `history` | Manually uploaded session files |
| `demo` | Generated sample data |

The first three sources can overlap; do not add their totals together. Estimated costs use reported values when available or the dated rate card in [ClaudePricing.cs](src/TokenScope.Api/Shared/Usage/ClaudePricing.cs). Rates are not fetched automatically. These estimates do not reconstruct subscription charges or historical invoices.

The app retains allowlisted usage metadata. Analytical rows exclude prompts, responses, tool inputs, raw payloads, and file paths, but optional attribution can contain private names. Review output before sharing it. The live queue is held in memory, so a crash can lose accepted events that have not reached the database. There is no automatic retention policy.

See [architecture](docs/ARCHITECTURE.md), [contributing](CONTRIBUTING.md), and [security](SECURITY.md) for development and operating details. TokenScope is licensed under [MIT](LICENSE). The container also includes [license notices for its dependencies](licenses/README.md).
