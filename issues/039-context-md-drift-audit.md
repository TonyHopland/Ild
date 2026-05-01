## What to build

`CONTEXT.md` documents the codebase's domain language, architecture, and module boundaries. Over the course of #001–#028 a number of things changed (new middleware, typed `HttpClient`, `Storage:*` config, `GetTemplateGraphByVersionIdAsync`, `GetByIdsAsync`, `AiProviderException`, lazy-loaded routes, ErrorBoundary, etc.).

Read `CONTEXT.md` end-to-end and reconcile it with the current code. Where the code has diverged, either update the doc or file a follow-up issue describing the gap.

## Acceptance criteria

- [x] Each section of `CONTEXT.md` either matches the code or links to a follow-up issue
- [x] New API Versioning Policy section (already appended) is cross-referenced from the API section
- [x] Storage layout section reflects `Storage:DataRoot` / `Storage:WorktreesSubdir` / `Storage:DatabaseFile`
- [x] SignalR section lists the typed events from #035 (or notes the work as pending)

## Blocked by

None. Spun out of #028 part E.
