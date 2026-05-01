## What to build

Tests cover services in isolation but skip the API surface and may share state.

**A — Controller integration tests:** Add `WebApplicationFactory<Program>`-based tests for at least:

- Auth: login success/failure, protected endpoint without token, protected endpoint with token
- Work items: create → list → update → transition → delete happy path
- Loop runs: start → poll status → cancel
  Use a per-test SQLite file or in-memory provider so tests are isolated.

**B — Test database isolation:** Audit `EngineHarness` and `TestDb` (in `ILD.Tests/`) for shared state. Each test should get a fresh DB context and a unique SQLite path (or `:memory:` connection). Document the convention.

**C — `useSignalR` and auth flow tests:** PRD's testing section calls out hooks and auth as test targets. Add tests for `useSignalR` (connection lifecycle, reconnect, handler registration) and for the login/logout flow including token persistence (paired with #11).

## Acceptance criteria

- [x] At least one integration test class per controller listed above, using `WebApplicationFactory<Program>` — _done in #038_
- [x] No two tests share a SQLite file or a singleton-cached service unless explicitly intended — _done in #038_
- [x] `useSignalR` has unit tests covering connect, disconnect, reconnect-on-token-change, and handler add/remove
- [x] Login flow has at least one component-level test in addition to backend `AuthServiceTests`

## Blocked by

- #15 (recovery tests) — overlapping infrastructure
- #11 (useSignalR fixes) — tests should target the corrected behavior
