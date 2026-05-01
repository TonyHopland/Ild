## What to build

Frontend silently drops errors and leans on `any` for SignalR payloads.

**A — Typed SignalR messages:** [Taskboard.tsx](frontend/src/pages/Taskboard.tsx#L21-L43), `LoopRunMonitor`, and `LoopEditor` use `as any` on every handler. Define a discriminated union (e.g. `SignalRMessage = { type: "NodeStateChanged"; payload: NodeStateChangedPayload } | ...`) in `frontend/src/types/` and update `useSignalR` / handlers to consume it. Pairs with issue #4 (typed hub messages on the server side).

**B — Error boundary:** [App.tsx](frontend/src/App.tsx) has no `<ErrorBoundary>`; render errors white-screen the app. Add a top-level boundary plus per-route boundaries for the heavy pages (`LoopEditor`, `LoopRunMonitor`).

**C — Centralized error reporting:** ~20 components call `console.error("Failed to ...")` with no UI feedback ([WorkItemModal.tsx](frontend/src/components/WorkItemModal.tsx#L99-L167), [Taskboard.tsx](frontend/src/pages/Taskboard.tsx#L64), [LoopEditor.tsx](frontend/src/pages/LoopEditor.tsx#L87-L250)). Build a `useErrorReporter()` hook + toast/banner host so failures surface to the user.

**D — Accessibility on modals and drag-and-drop:** `WorkItemModal` and other dialogs need `role="dialog"`, `aria-modal`, focus trap, and Escape-to-close. Drag-and-drop on the taskboard needs a keyboard alternative (e.g. arrow-keys to move cards between columns).

## Acceptance criteria

- [x] No `as any` casts in SignalR handlers; payloads use shared types from `frontend/src/types/` — _done in #035_
- [x] `<ErrorBoundary>` wraps the router and key heavy routes
- [x] User-visible error UI replaces silent `console.error` in WorkItemModal, Taskboard, LoopEditor, LoopRunMonitor — _done in #036_
- [x] Modals trap focus, close on Escape, and have correct ARIA roles
- [x] Taskboard supports moving a card via keyboard — _done in #037_

## Blocked by

- #4 (SignalR message shapes)
