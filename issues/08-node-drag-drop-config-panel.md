## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Add node drag-and-drop from a sidebar palette onto the React Flow canvas, and a side panel for configuring selected node properties. Each node type has type-specific configuration fields:

- Cmd: command string, timeout seconds
- AI: prompt template, AI provider selection, tool allowlist
- Human: input label
- Start: create worktree toggle
- Cleanup: (no extra config)
- PR: (no extra config)

New nodes are added to the template graph. The side panel validates node label is non-empty.

## Acceptance criteria

- [x] Sidebar palette shows draggable node type items (Start, Cmd, AI, Human, PR, Cleanup)
- [x] Dragging a node type onto the canvas creates a new node at the drop position
- [x] Clicking a node selects it and opens a configuration side panel
- [x] Side panel shows: node label (text input), node type (read-only), and type-specific fields
- [x] Cmd node config: command (text input), timeout (number input, default 30s)
- [x] AI node config: prompt template (textarea), AI provider dropdown, tool allowlist checkboxes
- [x] Start node config: "Create worktree" toggle (default on)
- [x] Deleting a node is supported via a delete button in the side panel
- [x] Node label validation: non-empty, unique within template
- [x] Frontend tests cover: drag-and-drop creates node, side panel opens on selection, config fields render per type, validation on empty label
- [x] `vp check` and `vp test` pass

## Blocked by

None - #07 is complete; can start immediately
