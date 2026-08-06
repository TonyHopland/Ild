# Contributing to ILD

Thanks for your interest in improving ILD. This guide covers how to get set up, the
checks your change must pass, and the conventions the project follows.

## Getting started

ILD runs as a split architecture (ILD API + WorkItem Server + PostgreSQL). The fastest
way to develop is to run the database and WorkItem Server from compose and run the app
and frontend from the host. See **[docs/development.md](docs/development.md)** for the
full setup, and **[docs/architecture.md](docs/architecture.md)** for how the pieces fit
together.

```bash
docker compose up postgres workitem-server      # infrastructure
export ILD_PASSWORD=letmein
export ILD_DB_CONNECTION_STRING='Host=localhost;Port=5432;Database=IldCore;Username=ild_core;Password=ild_core_password'
dotnet run --project ILD.Api                      # backend
cd frontend && vp install && vp dev               # frontend (Vite+ `vp` CLI)
```

> The frontend uses **Vite+** (the global `vp` CLI). Do not use raw `pnpm`/`npm`/`npx`
> or reach into `node_modules`. See [AGENTS.md](AGENTS.md).

## Validation — run before opening a PR

Your change must pass the same checks CI runs ([.github/workflows/ci.yml](.github/workflows/ci.yml)):

```bash
# Backend
dotnet build ILD.sln --configuration Release
dotnet test ILD.Tests/ILD.Tests.csproj

# Frontend
cd frontend
vp check          # format + lint + type-check
vp test --run     # one-shot test run
```

New behaviour should come with tests. The suite covers loop execution, recovery,
polling, repository management, auth, provider adapters, secret handling, metrics, schema
validation, and frontend page/component behaviour.

## Database migrations

**Never hand-write or edit EF Core migration files** — they are generated artifacts.
Scaffold them from model changes:

```bash
dotnet ef migrations add <MigrationName> --project ILD.Data --startup-project ILD.Api
```

Review the generated migration before committing it, and commit it alongside the model
change.

## Pull requests

- Branch off `main` and open a PR against `main`; keep PRs focused.
- Make sure all checks above pass locally.
- Update the relevant docs under `docs/` when you change behaviour or configuration.
- Add an entry under the `[Unreleased]` section of [CHANGELOG.md](CHANGELOG.md)
  (the project follows [Keep a Changelog](https://keepachangelog.com) and
  [Semantic Versioning](https://semver.org)).
- Match the style and idioms of the surrounding code.

## Reporting bugs and proposing features

Open a GitHub issue with enough context to reproduce or understand the request. For
**security issues, do not open a public issue** — follow [SECURITY.md](SECURITY.md).

## Code of Conduct

This project has a [Code of Conduct](CODE_OF_CONDUCT.md). By participating you agree to
uphold it.

## License

By contributing, you agree that your contributions are licensed under the project's
[MIT License](LICENSE).
