## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Add edge drawing between nodes on the React Flow canvas. Users connect nodes by dragging from a source node's output handle to a target node's input handle. Edge configuration includes type (`on_success` or `on_failure`) and max traversals.

The graph supports cycles (back-edges), enabling iterative workflows like AI -> Cmd -> Human -> back to AI. Each node can have at most one `on_success` edge and at most one `on_failure` edge.

## Acceptance criteria

- [ ] Nodes have visible output and input connection handles
- [ ] Dragging from output handle to another node's input handle creates an edge
- [ ] Edge creation opens a configuration dialog: edge type (OnSuccess/OnFailure), max traversals (number, default unlimited)
- [ ] Enforcing constraint: max one `on_success` and max one `on_failure` edge per source node
- [ ] Visual distinction: `on_success` edges are solid green, `on_failure` edges are dashed red
- [ ] Cycles are allowed (no cycle detection blocking edge creation)
- [ ] Deleting an edge is supported (click edge to select, delete key or button)
- [ ] Edge data serializes to `LoopNodeEdge` DTO format (sourceId, targetId, edgeType, maxTraversals)
- [ ] Frontend tests cover: edge creation, edge type selection, max traversals config, constraint enforcement (reject duplicate edge type), edge deletion
- [ ] `vp check` and `vp test` pass

## Blocked by

- Blocked by #08 (Node drag-drop and config panel must exist first)
