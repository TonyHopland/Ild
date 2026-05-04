# Issue #2 — Frontend: NodeTimeline + NodeItem + EdgeArrow Components

## What to build

Build the core timeline UI components that replace the old two-panel layout (Node Flow panel + Events panel) with a single vertical node timeline.

The timeline is a scrollable list where each `LoopRunNode` execution is rendered as a `NodeItem`. Items are connected by `EdgeArrow` connectors that show the edge type (OnSuccess/OnFailure) with color and label.

### Components

- **`NodeTimeline`** — Vertical list container. Handles smart auto-scroll: scrolls to new nodes only when the user is at the bottom. Shows a "new nodes" indicator when scrolled up.
- **`NodeItem`** — Accordion item. Collapsed: type icon, label, status indicator, duration. Expanded: delegates to child sections (built in issue #3). Only one item expanded at a time. The currently running node auto-expands.
- **`EdgeArrow`** — Vertical connector between nodes. Colored line (green for OnSuccess, red for OnFailure) with a "success" / "failure" badge label.

### Behavior

- Nodes appear as they execute (no pending/ghost nodes — the path cannot be predicted from conditional edges)
- Running node is auto-expanded with pulse/glow animation
- Accordion: expanding one node collapses all others
- Smart auto-scroll: if viewport is at bottom, scroll to new nodes; if user scrolled up, stay put and show indicator

### Styling

- Sticky header at top (back link, run ID, status badge, Pause/Resume/Cancel controls)
- Full-width scrollable timeline below
- All styles in component-scoped CSS or a dedicated stylesheet

## Acceptance criteria

- [ ] `NodeTimeline` component renders a vertical list of `LoopRunNode` executions
- [ ] `NodeItem` collapsed state shows: type icon, label, status indicator, duration
- [ ] `NodeItem` accordion behavior: only one expanded at a time
- [ ] Running node auto-expands with visual indicator (pulse/glow)
- [ ] `EdgeArrow` shows colored line + "success" / "failure" badge between nodes
- [ ] Smart auto-scroll: scrolls to new nodes only when user is at bottom
- [ ] "New nodes" indicator appears when user is scrolled up and new nodes arrive
- [ ] Sticky header with back link, status badge, and run controls
- [ ] Component works with mock data for initial verification

## Blocked by

- Blocked by #1 (requires new `LoopRunNode` type with per-execution semantics)
