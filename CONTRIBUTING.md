# Contributing

Open an issue for a bug or a proposed behavior change. Include the steps to reproduce, relevant versions, and a sanitized error. Do not attach personal Claude sessions, credentials, or unredacted usage exports. Report security issues using [SECURITY.md](SECURITY.md).

## Development setup

Install Docker with Linux container support, the .NET 10 SDK selected by `global.json`, Node.js 26 LTS, and PowerShell 7 for the helper scripts.

From the repository root, start the database and native API:

```powershell
docker compose up -d --wait timescaledb
$env:ConnectionStrings__tokenscope = 'Host=localhost;Port=55432;Database=tokenscope;Username=postgres;Password=postgres'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/TokenScope.Api
```

In another terminal, start the client:

```sh
cd web
npm ci
npm run dev
```

Vite serves `http://localhost:5199` and proxies `/api` to the API on `5299`. Stop the containerized app before using the native API on the same port. If you change the database port, update the connection string.

## Checks

Run from the repository root:

```sh
dotnet build TokenScope.slnx -c Release
dotnet test TokenScope.slnx -c Release --no-build
dotnet format whitespace TokenScope.slnx --verify-no-changes
```

Run `npm ci` and `npm run build` in `web` for TypeScript checks and the client build. Database integration tests need a disposable database; use the helper rather than pointing them at retained usage:

```powershell
.\scripts\verify-database.ps1
.\scripts\verify-isolated.ps1
.\scripts\verify-local-usage.ps1
```

The helpers cover database behavior, the production container and HTTP contracts, and growing local session files. Use synthetic fixtures for tests. `verify.ps1` provides read-only checks against a running instance by default; its fixture-import and demo-pulse switches deliberately insert data into their target instance.

Follow `.editorconfig` and the [architecture conventions](docs/ARCHITECTURE.md). Keep endpoints and SQL within their feature slice, parameterize values, and preserve source separation and unknown-cost handling. Add tests for behavior changes and include the checks you ran in the pull request. Contributions use the repository's [MIT license](LICENSE).

Before merging into `main`, bring your branch up to date and wait for the `verify` check to pass on the latest commit.
