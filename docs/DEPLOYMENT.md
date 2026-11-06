# Deploy TokenScope

The Linux container serves the React client and .NET API on port `8080`. Configure an Npgsql connection string to use self-hosted TimescaleDB or Tiger Cloud.

## Database requirements

Use a dedicated PostgreSQL database with TimescaleDB installed and enabled. The bundled Compose file pins the database version used for local development. The [schema](../src/TokenScope.Api/Infrastructure/Database/schema.sql) uses hypertables, continuous aggregates, hierarchical rollups, and refresh policies. Plain PostgreSQL cannot run it; validate compatibility before choosing another TimescaleDB version.

If the server extension is installed but not enabled in the target database, an administrator can run:

```sql
CREATE EXTENSION IF NOT EXISTS timescaledb;
```

The app applies its schema at startup. Its role needs database `CONNECT`, schema `USAGE`/`CREATE`, and ownership of application objects. It must be able to create and alter tables, indexes, continuous aggregates, and refresh policies, and refresh aggregates. A read-only or DML-only role is insufficient. There is no separate migration connection setting.

Back up retained data before upgrading, and test the new schema against a separate database first.

## Container configuration

Create a private, uncommitted `.env` file:

```dotenv
ConnectionStrings__tokenscope=Host=<database-host>;Port=5432;Database=<database>;Username=<user>;Password=<secret>;SSL Mode=VerifyFull
```

`ConnectionStrings__Database` is the fallback when `ConnectionStrings__tokenscope` is absent or blank. An invalid nonblank primary value fails startup. Supply Npgsql connection-string fields; a `postgres://` URI is not accepted unchanged.

For remote databases, `SSL Mode=VerifyFull` verifies the certificate chain and hostname. If the server uses a private certificate authority, mount its CA certificate and set Npgsql's `Root Certificate` field. Use your deployment platform's secret configuration where available. Docker administrators can inspect container environment values.

Build and run locally:

```sh
docker build -t tokenscope:local .
docker run -d --name tokenscope --restart unless-stopped \
  --env-file .env \
  -p 127.0.0.1:5299:8080 \
  tokenscope:local
```

Open [localhost:5299](http://localhost:5299). Container `localhost` refers to the container itself. Use a database hostname reachable from that container or a service name on its Docker network.

The bundled `docker compose up --build -d` starts both the app and a local database. Its example credentials and loopback ports are for local use. Database files are stored in a named volume; `docker compose stop` preserves it.

No GHCR image has been published yet. After a release is available, the image reference will be `ghcr.io/m-jovanovic/tokenscope:<version>`. Use a verified version tag or digest. Private packages require registry authentication.

## Collection and MCP

For the default port, start Claude Code from the repository root:

```sh
claude --settings ./claude-telemetry.json
claude mcp add --transport http tokenscope http://localhost:5299/mcp
```

Telemetry uses OTLP/HTTP JSON at `/v1/logs`; MCP uses `/mcp`. If Claude runs elsewhere, configure an endpoint reachable from that machine. Select **Live telemetry** in the dashboard and complete a real Claude turn to verify delivery.

Optional file collection requires a read-only mount, `LocalUsage__Enabled=true`, and `LocalUsage__ClaudePath` set to the container path. Add those settings to your existing deployment while preserving its database connection. See [local collection](LOCAL-USAGE.md).

## Network access

TokenScope has no built-in login, API authentication, or per-user isolation. Keep the default loopback binding for personal use. For access from other machines, protect all routes with an authenticated gateway or use a trusted private network. Protect the ingestion, import, and MCP endpoints as well as the dashboard.

Browser origin checks do not authenticate non-browser clients. Serve the UI and API through the same origin. A reverse proxy must preserve the browser's original `Host` header, including a non-default port; replacing it with an internal service name causes browser writes to return HTTP 403. Configure TLS and any gateway authentication for both Claude's exporter and MCP client.

MCP report restrictions apply to TokenScope's tools. They do not make the application's connection role read-only or restrict other tools available to an assistant.

## Health and stored data

```sh
curl --fail http://localhost:5299/health
```

`/health` checks database connectivity. `/api/status` includes database metadata and ingestion counters. A healthy response does not establish that telemetry is arriving or that historical aggregates have caught up.

Usage rows, replay keys, and collector checkpoints are stored in PostgreSQL. Back them up together, monitor disk space, and test restoration. The app installs no retention policy. The application container does not need its own data volume for this state.

The ingestion queue and upload previews are held in memory. A restart discards pending previews; a crash can lose accepted live events that have not been persisted. The batch writer retries database failures, but accepted data is not durably queued. Collector source files remain read-only on their original machine.

## Upgrades and releases

For an upgrade, review the schema changes, back up the database, and test the image against a separate database. Replace the app container with the same connection and collector mounts, then check health, source counts, aggregate refresh status, and a new Claude event. Reverting the container image does not revert schema changes; database recovery may require a backup.

To release an image, tag a verified commit on `main` with a version such as `v1.0.0`. The [image workflow](../.github/workflows/publish-image.yml) runs the test suite, then builds and pushes `linux/amd64` and `linux/arm64` images to `ghcr.io/m-jovanovic/tokenscope`.

GitHub manages repository and package visibility separately. Before announcing a public image, check that the version tag exists in GHCR and pull it without registry credentials.
