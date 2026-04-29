## What to build

Several frontend features exist in the backend API but have no UI:

**A — Pause/Resume controls:** Backend supports `POST /loopruns/{id}/pause` and `POST /loopruns/{id}/resume`. Add Pause and Resume buttons to LoopRunMonitor.

**B — Dependency management UI:** Backend supports `GET/POST/DELETE /workitems/{id}/dependencies`. Add UI in WorkItemModal to add/remove dependencies with clickable links to dependent WorkItems.

**C — Work item delete:** Backend has `DELETE /workitems/{id}`. Add a delete button to WorkItemCard and WorkItemModal.

**D — WorkItemCard not clickable:** PRD user story 20 says clicking a work item opens its detail modal. The card is draggable but has no `onClick` handler.

**E — Work item transition:** Backend has `POST /workitems/{id}/transition`. Frontend should use this endpoint (not a full PUT) for status changes, to enforce server-side state machine validation.

## Acceptance criteria

- [ ] LoopRunMonitor has Pause and Resume buttons wired to `loopRunService`
- [ ] WorkItemModal shows dependency list with clickable links to dependent WorkItems
- [ ] WorkItemModal has UI to add/remove dependencies
- [ ] WorkItemCard opens WorkItemModal on click (separate from drag)
- [ ] WorkItemCard and WorkItemModal have a delete button with confirmation
- [ ] Status changes use `POST /workitems/{id}/transition` instead of full PUT
- [ ] `loopRunService` has `pause` and `resume` methods

## Blocked by

- Blocked by #1 (Fix LoopRun API endpoints — loopRunService must be correct first)
- Blocked by #4 (Fix SignalR message shapes — real-time updates needed for pause/resume feedback)
