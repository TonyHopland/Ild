## What to build

The frontend test suite has only 2 tests. The PRD and README indicate the frontend should have Vitest tests. Add tests for:

**A — API service layer:** Test that `api.ts` correctly handles auth tokens, error responses, and URL construction.

**B — Auth flow:** Test login, logout, token persistence, and session expiration.

**C — SignalR hook:** Test that `useSignalR` connects, disconnects, reconnects on auth change, and deduplicates handlers.

**D — Taskboard component:** Test that columns render correctly, work items appear in the right columns, and SignalR updates trigger re-renders.

## Acceptance criteria

- [x] API service tests verify correct URL construction and error handling
- [x] Auth service tests verify token storage, retrieval, and expiration
- [x] `useSignalR` hook tests verify connection lifecycle and handler deduplication
- [x] Taskboard component tests verify column rendering and work item placement
- [x] Tests run via `vp test --run` and pass
- [x] Test count increased from 2 to at least 10

## Blocked by

- Blocked by #11 (Fix React hook issues — test the corrected hook behavior)
