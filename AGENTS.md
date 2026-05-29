<!--VITE PLUS START-->

# Using Vite+, the Unified Toolchain for the Web

This project is using Vite+, a unified toolchain built on top of Vite, Rolldown, Vitest, tsdown, Oxlint, Oxfmt, and Vite Task. Vite+ wraps runtime management, package management, and frontend tooling in a single global CLI called `vp`. Vite+ is distinct from Vite, and it invokes Vite through `vp dev` and `vp build`. Run `vp help` to print a list of commands and `vp <command> --help` for information about a specific command.

Docs are local at `node_modules/vite-plus/docs` or online at https://viteplus.dev/guide/.

## Review Checklist

- [ ] Run `vp install` after pulling remote changes and before getting started.
- [ ] Run `vp check` and `vp test` to format, lint, type check and test changes.
- [ ] Check if there are `vite.config.ts` tasks or `package.json` scripts necessary for validation, run via `vp run <script>`.

<!--VITE PLUS END-->

## Running the frontend tests

`vp` is a **global** CLI — invoke it directly. Do NOT reach into `node_modules`
(`node_modules/.bin/vite-plus`, `node_modules/vite-plus/dist/cli.js`) and do NOT
use `pnpm vite-plus`/`npm test`/`npx vitest` — those paths do not exist or are
not wired up here and will only waste time.

```bash
cd frontend && vp test                 # watch mode
cd frontend && vp test --run           # one-shot (CI / agents)
cd frontend && vp test --run src/utils/__tests__/workItemJson.test.ts   # single file
cd frontend && vp check                # format + lint + type-check
```

.NET tests: `dotnet test ILD.Tests/ILD.Tests.csproj`.

## Database Migrations

NEVER manually write or edit EF Core migration files. Always use the EF Core CLI tools to scaffold migrations from model changes:

```bash
dotnet ef migrations add <MigrationName> --project <project-with-dbcontext>
dotnet ef database update --project <project-with-dbcontext>
```

Manually editing migration files is error-prone and will cause schema drift. The migration files are generated artifacts, not source of truth.
