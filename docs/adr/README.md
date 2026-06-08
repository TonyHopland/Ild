# Architecture Decision Records

Short records of architectural decisions that are **hard to reverse, surprising without context, and the result of a real trade-off**. Each captures _that_ a decision was made and _why_ — not implementation detail (that lives in [CONTEXT.md](../../CONTEXT.md)).

| ADR                                                   | Decision                                                             |
| ----------------------------------------------------- | -------------------------------------------------------------------- |
| [0001](./0001-standalone-workitem-server.md)          | Standalone WorkItem Server as the work-item source of truth          |
| [0002](./0002-manual-api-versioning.md)               | Manual `/api/v1` prefix instead of `Asp.Versioning`                  |
| [0003](./0003-per-edge-traversal-limits.md)           | Runaway-graph safety net is per-edge, not per-node                   |
| [0004](./0004-pr-lifecycle-as-graph-node.md)          | PR lifecycle is an explicit graph node, not implicit engine behavior |
| [0005](./0005-template-resolved-per-run-from-tags.md) | Loop template is resolved per-run from WorkItem tags                 |
| [0006](./0006-run-isolation-clean-origin-base.md)     | Every run starts from a clean `origin/<default>` base                |
| [0007](./0007-ai-execution-delegated-to-adapters.md)  | AI execution is delegated wholesale to pluggable CLI adapters        |

New ADRs use the next sequential number; see the format in `.agents/skills/grill-with-docs/ADR-FORMAT.md`.
