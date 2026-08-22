# The runaway-graph safety net counts AI steps between human interactions, globally

The loop engine bounds a runaway graph with **one** number per run: how many AI
nodes it has executed since a human last touched it, capped by the
`ai.maxTraversals` app setting (default 25). Reaching the cap **parks** the run
for a person — `WaitingHuman` + `IsHalted`, stamped `HaltReason.MaxAiTraversals`
— offering the same continue / continue-with-guidance / abandon controls every
other halt does. It replaces the per-edge limit of [ADR-0003](./0003-per-edge-traversal-limits.md),
which this ADR supersedes.

Per-edge limits were both too weak and too confusing to be the safety net. Too
weak, because the thing worth bounding is spend and unattended drift, and a
cycle through five nodes buys five times its per-edge budget before anything
notices — the number a person configures bears no relation to the number of AI
steps it permits. Too confusing, because the limit lived on the edges of a graph
the person drew, so tuning it meant reasoning about a cycle's shape rather than
about how long they are willing to let an agent run alone.

Counting only AI nodes and resetting on human interaction is what makes a single
global number safe to set. A Cmd or Condition node costs nothing and is not what
runs away; an AI node spends money and can loop unattended. And a conversational
graph — grill-me planning, an AI ↔ Human review loop — legitimately revisits its
nodes forever, which is exactly the case a per-edge count was introduced to
protect and exactly the case a naive global count would break. Resetting the
counter whenever the run passes through a person (a park at a Human node, a PR
waiting to be merged, and every resume a human triggers) means the cap measures
only unattended stretches, so a long conversation never approaches it however
many turns it runs.

## Considered options

**Fail the run at the cap, as the per-edge limit did.** Rejected: the cap is not
evidence the run is broken, only that nobody has looked at it lately. Failing
discards a worktree of real work over a number the operator picked by guessing.
Parking asks the question the number actually encodes — "is this still going
somewhere?" — and the resume paths to answer it already existed for the Halt and
provider-throttle parks, so parking added no new API surface.

**Keep the count in memory, rebuilt from `LoopRunNode.IncomingEdgeId` on
recovery.** That was ADR-0003's arrangement and it worked, but a single per-run
integer has somewhere to live: a column on `LoopRun`. Restart-safety then falls
out of the row rather than out of a reconstruction that has to agree with the
counting rule forever.

**A per-node or per-run total execution cap.** Both were rejected in ADR-0003
and are still wrong for the same reason: they cannot tell a productive
conversation from a spin, because they count without asking whether anyone is
watching. Resetting on human interaction is the missing distinction, and once
you have it the count no longer needs to be per-edge to be safe.

## Consequences

- **Resume refills the budget.** Not a convenience: without it "just continue"
  would re-trip the cap on the very next node and the park would be a dead end.
  Continuing therefore buys another full budget, and a person who keeps clicking
  Resume can run indefinitely — which is the correct answer, because they are in
  the loop by definition.
- **A PR awaiting merge counts as human interaction.** A parked PR is in a
  person's hands, so the budget refills there. The cost is that a PR node
  looping through a fix cycle (`on_ci_failed` → AI → PR → …) refills every lap
  and so is bounded only by the person watching the PR, not by this cap.
- **The counter is visible.** Both run endpoints project `aiTraversalCount`, and
  the steer window shows it on a capped park, so the question a person is being
  asked comes with the number behind it.
- **Existing per-edge values are dropped** with the `LoopNodeEdge.MaxTraversals`
  column. Nothing read them once the global cap landed, and no template's
  behaviour depended on a value the editor offered but few loops set.
