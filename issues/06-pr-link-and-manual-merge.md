## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Add PR link display and manual merge override to the WorkItemModal. When a WorkItem has an associated PR, show the PR URL as a clickable link. Provide "Link PR" and "Mark Merged" buttons for cases where webhooks are unavailable.

The "Link PR" button accepts a PR URL and associates it with the WorkItem. The "Mark Merged" button manually triggers the merge state transition, moving the WorkItem toward Done via Cleanup.

## Acceptance criteria

- [x] WorkItem detail modal displays PR URL as a clickable link when `WorkItem.PrUrl` is set
- [x] "Link PR" button opens an input to manually associate a PR URL with the WorkItem
- [x] "Mark Merged" button calls `POST /api/v1/workitems/{id}/mark-merged` to trigger state transition
- [x] Mark Merged is only enabled when a PR URL is associated
- [x] Backend handles manual merge: updates WorkItem status, triggers Cleanup node execution
- [x] Backend tests cover: manual merge transitions WorkItem toward Done, PR linking persists URL
- [x] Frontend tests cover: PR link display, Link PR form, Mark Merged button state
- [x] `vp check` and `vp test` pass

## Blocked by

None - #01 is complete; can start immediately
