## What to build

Repository-level cleanup that does not fit into a single feature issue.

**A — Single solution file:** Both [ILD.sln](ILD.sln) and [ild.slnx](ild.slnx) are committed. Pick one (`.slnx` is the newer XML format) and delete the other.

**B — Verify root vs frontend Vite config:** Both [vite.config.ts](vite.config.ts) and [frontend/vite.config.ts](frontend/vite.config.ts) exist. Tracked also under #26; remove the unused one.

**C — Secrets in `appsettings.Development.json`:** Confirm no real credentials are committed and that the README documents the env-var-only auth model (`ILD_USERNAME` / `ILD_PASSWORD`). Add a note about API keys living in `AiProvider.Config` and never being committed.

**D — CI workflow:** No GitHub Actions workflow is present. Add one running:

- `vp install`
- `vp check` (format + lint + types)
- `vp test` (frontend)
- `dotnet test` (backend)
  Use `voidzero-dev/setup-vp@v1` per `AGENTS.md`.

**E — `CONTEXT.md` drift check:** Walk through `CONTEXT.md` and confirm the module list, state machine diagram, and naming conventions still match the implementation. Update or open follow-up issues for divergences.

## Acceptance criteria

- [x] Only one solution file remains and builds via `dotnet build`
- [ ] Only one `vite.config.ts` remains — _kept both intentionally; see #026 acceptance note_
- [x] `appsettings.Development.json` contains no secrets; README documents auth and API-key handling
- [x] CI workflow runs `vp check`, `vp test`, and `dotnet test` on push and PR
- [x] `CONTEXT.md` either matches the code or has follow-up issues filed for the gaps — _done in #039_

## Blocked by

None.
