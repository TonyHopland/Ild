## What to build

Frontend SignalR handlers in `Taskboard`, `LoopRunMonitor`, and elsewhere still use `as any` casts on incoming payloads. Define typed DTOs that mirror the backend SignalR event shapes, share them through `frontend/src/types/`, and remove the casts.

Backend events (from `ILD.Api/Hubs/`):

- `NodeStateChanged`
- `LoopRunStateChanged`
- `EventLogged`
- `WorkItemStateChanged`
- `HumanFeedbackRequired`

## Acceptance criteria

- [x] One TypeScript interface per event, exported from `frontend/src/types/signalr.ts`
- [x] `useSignalR.on<T>(event, handler)` is generically typed
- [x] No `as any` remains in `Taskboard.tsx`, `LoopRunMonitor.tsx`, or any other consumer
- [x] Frontend tests still pass

## Blocked by

None. Spun out of #025 part A.
