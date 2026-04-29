## Parent

PRD.md

## Status

**READY**

## What to build

Build an Event Log viewer page that displays the append-only event stream for a LoopRun. Events show timestamp, event type, node reference, and message. For AI node events, the AI context used is expandable. Pagination uses cursor-based navigation instead of skip/take.

The page is accessible from the LoopRun monitor and from the WorkItem's run history.

## Acceptance criteria

- [ ] New route `/loop-runs/{runId}/events` or integrated into LoopRunMonitor
- [ ] Event list displays: sequence number, timestamp, event type badge, node reference, truncated message
- [ ] Clicking an event expands to show full message and AI context (for AI node events)
- [ ] Cursor-based pagination: "Load More" button fetches next page using cursor from last event
- [ ] API endpoint `GET /api/v1/loopruns/{id}/events?cursor={cursor}&limit={limit}` returns paginated events
- [ ] Backend replaces skip/take with cursor-based pagination in `EventLogService`
- [ ] Large payload events (>10KB) show a "load payload" button that fetches from disk
- [ ] Frontend tests cover: event list rendering, expand/collapse, cursor pagination loads more, AI context display
- [ ] Backend tests cover: cursor pagination returns correct pages, sequence ordering is monotonic
- [ ] `vp check` and `vp test` pass

## Blocked by

None - can start immediately
