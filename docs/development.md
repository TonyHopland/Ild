# Development

Local development uses the same split architecture as production: the ILD API, the WorkItem Server, and a PostgreSQL database. The easiest way to satisfy infrastructure locally is to run the database and WorkItem Server from compose, then run the app and frontend from the host.

```bash
docker compose up postgres workitem-server
```

## Backend

```bash
export ILD_PASSWORD=letmein
export ILD_DB_CONNECTION_STRING='Host=localhost;Port=5432;Database=IldCore;Username=ild_core;Password=ild_core_password'
dotnet run --project ILD.Api
```

For the standalone WorkItem Server outside compose:

```bash
export WORKITEM_DB_CONNECTION_STRING='Host=localhost;Port=5432;Database=IldWorkitems;Username=ild_workitems;Password=ild_workitems_password'
export WORKITEM_API_KEYS=test-api-key-123
dotnet run --project ILD.WorkItemServer
```

## Frontend

This repo uses **Vite+** (the `vp` CLI). Do not use raw `pnpm`/`npm` installs.

```bash
cd frontend
vp install
vp dev
```

The frontend dev server runs on <http://localhost:3000> and proxies `/api` and `/hubs` to `http://localhost:5000` by default (override with `ILD_API_PROXY_TARGET`).

## Validation

```bash
dotnet build ILD.sln
dotnet test ILD.Tests/ILD.Tests.csproj

cd frontend
vp check          # format + lint + type-check
vp test --run     # one-shot test run
```

The test suite covers loop execution, recovery, polling, repository management, auth, provider adapters, metrics, schema validation, and frontend page/component behavior.

## Database migrations

Never hand-write or edit EF Core migration files — they are generated artifacts. Scaffold from model changes with the EF Core CLI:

```bash
dotnet ef migrations add <MigrationName> --project <project-with-dbcontext>
dotnet ef database update --project <project-with-dbcontext>
```

## QA preview

If a repository defines `preview.profiles` in `ild.config.json`, ILD can start and manage long-running QA services inside a worktree. The work-item modal exposes preview controls, and the same API surface is available to AI tools. `POST /api/v1/workitems/{id}/preview/start` accepts optional `profileName`, `skipInstall`, `publicHost`, `portOverrides`, and `timeoutSeconds` values.
