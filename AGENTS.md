<!--VITE PLUS START-->

# Using Vite+, the Unified Toolchain for the Web

This project is using Vite+, a unified toolchain built on top of Vite, Rolldown, Vitest, tsdown, Oxlint, Oxfmt, and Vite Task. Vite+ wraps runtime management, package management, and frontend tooling in a single global CLI called `vp`. Vite+ is distinct from Vite, and it invokes Vite through `vp dev` and `vp build`. Run `vp help` to print a list of commands and `vp <command> --help` for information about a specific command.

Docs are local at `node_modules/vite-plus/docs` or online at https://viteplus.dev/guide/.

## Built-in Commands vs Scripts

`vp <name>` runs a built-in command. `vp run <name>` runs a `package.json` script or a `vite.config.ts` task. Scripts cannot overwrite built-ins, so `vp dev` and `vp run dev` may do different things. Check `package.json` and `vite.config.ts` first, and run `vp run <name>` when the project defines a script or task with that name.

## Review Checklist

- [ ] Run `vp install` after pulling remote changes and before getting started.
- [ ] Run `vp check` and `vp test` to format, lint, type check and test changes.
- [ ] Check if there are `vite.config.ts` tasks or `package.json` scripts necessary for validation, run via `vp run <script>`.
- [ ] If setup, runtime, or package-manager behavior looks wrong, run `vp env doctor` and include its output when asking for help.

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

## Changelog Entries

`CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). An entry is for someone
skimming to find out what changed. It is not a review artifact, a post-mortem, or a record of the work you
just did.

**Write the absolute minimum: one bold sentence saying what is different now. Usually nothing else.** A
second sentence has to earn its place and rarely does — if the change does not land without it, the first
sentence is worded badly rather than pitched too short.

- Present tense, and only what a user would notice.
- Omit changes that are not user-visible. Refactors, test scaffolding and internal renames get no entry.
- No mechanism: no jobs, matrices, endpoints, schemas, file paths or symbol names. Not even a summary of
  it. This is the most tempting material because it is the freshest in your mind, and the least useful to
  a reader who cannot act on any of it.
- No links — not to ADRs, docs, or issues. Anyone who wants more has the commit history and the PR.
- No justification: not what you rejected, not why it was hard.
- The size of the change is not the size of the entry. A large change usually earns a _shorter_ one,
  because there is more mechanism to leave out.

Good:

```markdown
- **Stop a chat reply mid-flight.** A red stop square sits beside **Send** while a turn is running and
  cancels it.
```

Bad — the same change, written as a post-mortem:

```markdown
- **Stop a chat reply mid-flight.** The chat bubble shows a red stop square to the left of **Send** for
  exactly as long as a turn is in progress, cancelling it over the new `POST /api/v1/chat/{id}/interrupt`.
  The endpoint checks ownership before cancelling, exactly as the per-chat delete does, and the button does
  not clear the busy state itself: the server persists the partial reply and announces it, so an
  interrupted turn ends through the same hub events as one that finished on its own and the transcript
  keeps what the agent had produced (flagged `interrupted`). A stop that loses the race to the turn
  finishing is a no-op rather than an error.
```

Every clause in the bad version is true. None of it belongs in a changelog.

## Database Migrations

NEVER manually write or edit EF Core migration files. Always use the EF Core CLI tools to scaffold migrations from model changes:

```bash
dotnet ef migrations add <MigrationName> --project <project-with-dbcontext>
dotnet ef database update --project <project-with-dbcontext>
```

Manually editing migration files is error-prone and will cause schema drift. The migration files are generated artifacts, not source of truth.

## Code style

Code is the explanation. No comments narrating what a change does or why.
Comments only for non-obvious invariants or workarounds (with a link).
No summary paragraphs in the diff. Prefer better names over comments.
