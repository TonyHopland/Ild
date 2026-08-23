# Development

Local development uses the same split architecture as production: the ILD API, the WorkItem Server, and a PostgreSQL database. The easiest way to satisfy infrastructure locally is to run the database and WorkItem Server from compose, then run the app and frontend from the host.

```bash
cp .env.example .env   # then fill in the required secrets
docker compose up postgres workitem-server
```

Compose refuses to start until `.env` sets `POSTGRES_PASSWORD`, `ILD_DB_PASSWORD`,
`WORKITEM_DB_PASSWORD`, `WORKITEM_API_KEYS` and `ILD_SESSION_TOKEN_PEPPER`. The two
database passwords are written into the `ild_core` and `ild_workitems` roles the first
time the `postgres-data` volume is created, so the commands below reuse them rather
than naming a constant.

Postgres publishes no host port by default. **Uncomment the `ports` line on the
`postgres` service in `docker-compose.yml`** to run the backend from the host as
below, or to attach a psql/GUI client.

## Backend

```bash
export ILD_PASSWORD=letmein
export ILD_DB_CONNECTION_STRING="Host=localhost;Port=5432;Database=IldCore;Username=ild_core;Password=$ILD_DB_PASSWORD"
dotnet run --project ILD.Api
```

`ILD_SESSION_TOKEN_PEPPER` is optional for a direct run: unset, session tokens are
hashed unkeyed as they were before and startup says so. Export the same value your
`.env` uses if you want sign-ins to survive switching between the two.

For the standalone WorkItem Server outside compose:

```bash
export WORKITEM_DB_CONNECTION_STRING="Host=localhost;Port=5432;Database=IldWorkitems;Username=ild_workitems;Password=$WORKITEM_DB_PASSWORD"
export WORKITEM_API_KEYS=$(openssl rand -hex 32)
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
