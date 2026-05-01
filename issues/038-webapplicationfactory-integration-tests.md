## What to build

Backend tests today are mostly unit tests against in-memory SQLite. There are no end-to-end tests that boot the API through `WebApplicationFactory<Program>`, so route wiring, middleware ordering, auth, and DI registration are uncovered.

Also audit `TestDb` / `EngineHarness` to ensure no two test classes share a SQLite file, a cached HTTP client, or a singleton service instance unless explicitly intended.

## Acceptance criteria

- [x] At least one integration test class per controller (`AuthController`, `WorkItemsController`, `LoopRunsController`, `LoopTemplatesController`, `AiProvidersController`, `RepositoriesController`, `RemoteProvidersController`, `WebhookController`, `EventLogController`)
- [x] Each test seeds its own DB (per-test connection string or per-test `WebApplicationFactory` instance)
- [x] Audit doc / comment added to `TestDb.cs` describing isolation guarantees
- [x] Existing 138 unit tests continue to pass

## Blocked by

None. Spun out of #027 parts A and B.
