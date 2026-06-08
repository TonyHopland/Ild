# ADR-0003: Runaway-graph safety net is per-edge, not per-node

The loop engine bounds runaway graphs by counting traversals **per edge** (each edge's `MaxTraversals`, defaulting to `LoopEngine.DefaultMaxEdgeTraversals = 50` when null), not by capping how many times a node may execute or how many iterations a run may take. A per-edge limit lets legitimate conversational loops (e.g. AI ↔ Human) revisit a node many times while still catching a specific cycle that spins without progress — a per-node cap would either choke valid loops or miss tight cycles between two nodes.

## Consequences

- Each edge traversal is persisted on the destination `LoopRunNode.IncomingEdgeId`, so the in-memory count dictionary can be rebuilt after a process restart and the limit survives recovery.
- There is no template-level execution cap; bounding a whole template means tuning individual edges.
