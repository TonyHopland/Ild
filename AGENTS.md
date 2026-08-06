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

**Budget: one bold sentence naming the user-visible change, then at most two sentences. Roughly 60 words
total — the Good example below sits at the limit.** If it needs more than that, the depth belongs in an ADR
under `docs/adr/` or in `docs/`, and the entry links to it — `See [ADR-0017](docs/adr/0017-shutdown-halts-the-in-flight-ai-node.md).`

Count the words before you commit, and treat the budget as a limit rather than a target to argue with. The
size of the change does not license a longer entry: a big change usually earns a _shorter_ one that points
at an ADR, because there is more mechanism to leave out, not less.

- Lead with what is different now, in the present tense.
- Then, only if it is not obvious, one sentence of why or what it replaces.
- Omit changes that are not user-visible. Refactors, test scaffolding and internal renames get no entry.
- No file paths, no symbol names, no narration of the debugging that led there, no listing of the cases
  that did _not_ change.
- Do not explain the mechanism you just built — the jobs, matrices, endpoints, schemas or file layout. It
  is the most tempting material precisely because it is freshest, and it is the least useful to a reader
  who cannot act on any of it. It belongs in the ADR or the commit message.
- Resist the urge to justify. Every clause you want to add about the alternative you rejected is a clause
  the reader has to skip.

Good:

```markdown
- **Stop a chat reply mid-flight.** A red stop square sits beside **Send** while a turn is running and
  cancels it. The cancel primitive already existed, but was only reachable by sending another message
  or deleting the chat.
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
