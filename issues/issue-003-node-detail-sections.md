# Issue #3 — Frontend: NodeDetailPanel (3 Sections) + LiveStream

## What to build

Build the expanded node content sections that appear when a `NodeItem` is expanded. Each expanded node shows three sections in order: effective input, events, and output.

### Sections

- **`NodeInputSection`** — Displays the effective input for the node. For Cmd nodes: the shell command. For AI nodes: the prompt template + resolved context (work item title, description, prior event log summary). For Human nodes: the feedback request. For Start/Cleanup: a status message. Data comes from the structured `NodeStarted` event payload (issue #4).
- **`NodeEventsSection`** — Chronological list of all events for that specific execution instance, grouped by `RunNodeId` (not template `NodeId`). Events include `NodeStarted`, `NodeProgress`, `NodeCompleted`/`NodeFailed`, `EdgeTraversed`, `HumanFeedbackRequested`, etc. Large payloads (>10KB) use the existing "Load Payload" lazy-load pattern.
- **`NodeOutputSection`** — Final output in a collapsible `<pre>` block. Truncated after 20 lines with a "Show more" expand button.

### LiveStream

- **`LiveStream`** — Real-time output stream for the currently running node. Replaces the events section at the bottom of the expanded running node. Receives lines from `NodeProgress` SignalR events. Smart auto-scroll: scrolls to latest only if user is at bottom; shows "▼ new lines" indicator otherwise. On page refresh, stream is lost (ephemeral).

## Acceptance criteria

- [ ] `NodeInputSection` renders structured effective input per node type (Cmd, AI, Human, Start, Cleanup)
- [ ] `NodeEventsSection` shows chronological events filtered by `RunNodeId`
- [ ] `NodeOutputSection` truncates after 20 lines with "Show more" expand
- [ ] `LiveStream` receives and displays `NodeProgress` lines in real-time
- [ ] `LiveStream` smart auto-scroll: follows bottom only when user is at bottom
- [ ] "New lines" indicator in `LiveStream` when scrolled up
- [ ] Three sections render in order: Input → Events → Output (LiveStream replaces Events for running node)
- [ ] Large event payloads use lazy-load pattern (existing >10KB behavior)

## Blocked by

- Blocked by #1 (requires `RunNodeId` on events for proper grouping)
