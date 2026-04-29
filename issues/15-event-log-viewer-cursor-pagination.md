## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Build an Event Log viewer page that displays the append-only event stream for a LoopRun. Events show timestamp, event type, node reference, and message. For AI node events, the AI context used is expandable. Pagination uses cursor-based navigation instead of skip/take.

The page is accessible from the LoopRun monitor and from the WorkItem's run history.

## Acceptance criteria

- [x] New route `/loop-runs/{runId}/events` or integrated into LoopRunMonitor
- [x] Event list displays: sequence number, timestamp, event type badge, node reference, truncated message
- [x] Clicking an event expands to show full message and AI context (for AI node events)
- [x] Cursor-based pagination: "Load More" button fetches next page using cursor from last event
- [x] API endpoint `GET /api/v1/loopruns/{id}/events?cursor={cursor}&limit={limit}` returns paginated events
- [x] Backend replaces skip/take with cursor-based pagination in `EventLogService`
- [x] Large payload events (>10KB) show a "load payload" button that fetches from disk
- [x] Frontend tests cover: event list rendering, expand/collapse, cursor pagination loads more, AI context display
- [x] Backend tests cover: cursor pagination returns correct pages, sequence ordering is monotonic
- [x] `vp check` and `vp test` pass

## Blocked by

None - can start immediately
