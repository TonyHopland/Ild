# Issue #5 — Frontend: Rewrite EventLogViewer + SignalR Integration

## What to build

Replace the old two-panel `EventLogViewer` with the new node timeline view, wire up all SignalR handlers to the new components, and remove the legacy code.

### Changes

- Replace the `EventLogViewer` page's two-panel layout (`node-flow-panel` + `event-log-panel`) with the new `NodeTimeline` component from issue #2
- Wire `NodeTimeline` to render `NodeItem`s that expand to show content from issue #3
- Subscribe to `EventLogged` SignalR event and parse `NodeStarted` structured payloads to populate effective input on the running node
- Update existing SignalR handlers (`NodeStateChanged`, `LoopRunStateChanged`, `NodeProgress`) to work with the new per-execution data model
- Remove old inline styles, old component code, and unused imports

### SignalR handler mapping

- **`NodeStateChanged`** — Update node status. If `newStatus === Running` and no node exists for that `runNodeId`, create a new `NodeItem` in the timeline.
- **`LoopRunStateChanged`** — Update run status badge in header. When run completes/fails, collapse the running node to final state.
- **`NodeProgress`** — Append line to the running node's `LiveStream`.
- **`EventLogged`** — Parse `NodeStarted` structured payload. Attach effective input to the corresponding running node so `NodeInputSection` can render it.

### Cleanup

- Remove old `node-flow-panel` CSS and rendering code
- Remove old `event-log-panel` CSS and rendering code
- Remove unused `MergedNode` interface (no longer needed with per-execution model)
- Remove `normalizeLoopRunStatus` / `normalizeNodeStatus` if no longer needed

## Acceptance criteria

- [ ] `EventLogViewer` renders new `NodeTimeline` instead of two-panel layout
- [ ] `NodeStateChanged` creates new timeline entries for new executions
- [ ] `EventLogged` parses `NodeStarted` payload and populates effective input on running node
- [ ] `NodeProgress` appends to `LiveStream` in the running node
- [ ] `LoopRunStateChanged` updates header status badge
- [ ] Run completion collapses running node to final state with output section
- [ ] Old two-panel layout code and styles removed
- [ ] `vp check` passes (lint, typecheck, format)
- [ ] `vp test` passes

## Blocked by

- Blocked by #2, #3, #4 (all components and backend changes must be in place)
