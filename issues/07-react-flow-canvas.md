## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Replace the form-based LoopEditor with a React Flow canvas that renders the loop template's nodes and edges as a visual directed graph. Nodes are rendered as custom React Flow node components styled by node type (Start, Cmd, AI, Human, PR, Cleanup). The canvas supports pan, zoom, and node selection.

Load an existing LoopTemplate from the API and render its graph. This slice does not yet support editing -- it's a read-only visual representation.

## Acceptance criteria

- [x] React Flow (`@xyflow/react`) is installed as a dependency
- [x] LoopEditor page renders a React Flow canvas instead of the form-based editor
- [x] Custom node components exist for each NodeType: Start, Cmd, AI, Human, PR, Cleanup
- [x] Each node type has distinct visual styling (icon, color, label)
- [x] Edges are rendered between nodes according to `LoopNodeEdge` data
- [x] `on_success` and `on_failure` edges are visually distinguished (e.g., solid vs dashed line)
- [x] Canvas supports pan and zoom
- [x] Selecting a template from the list loads its graph onto the canvas
- [x] Frontend tests cover: canvas rendering, node count matches template, edge count matches template, node type styling
- [x] `vp check` and `vp test` pass

## Blocked by

None - #01 is complete; can start immediately
