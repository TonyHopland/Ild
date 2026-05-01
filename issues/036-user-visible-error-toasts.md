## What to build

Several components still call `console.error` and silently swallow user-facing failures. WorkItemModal's submit path now surfaces errors (#019), but the rest do not:

- `Taskboard` (drag/drop transition, dependency add/remove)
- `LoopEditor` (template save/load)
- `LoopRunMonitor` (pause/resume/cancel)

Introduce a small toast/banner component (or reuse an existing UI primitive) and route all user-visible failures through it.

## Acceptance criteria

- [x] A reusable `<Toast>` or `<ErrorBanner>` component lives under `frontend/src/components/`
- [x] At least the three pages above replace their bare `console.error` with a visible error
- [x] Errors are dismissable
- [x] Tests cover the error path on at least one page

## Blocked by

None. Spun out of #025 part C.
