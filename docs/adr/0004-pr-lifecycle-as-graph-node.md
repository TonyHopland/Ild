# ADR-0004: PR lifecycle is an explicit graph node, not implicit engine behavior

Creating a pull request, pushing the branch, waiting for merge/rejection webhooks, and routing on the outcome are modelled as a first-class **PR node** in the loop graph rather than baked into the engine as automatic end-of-run behavior. Making it explicit lets a template decide _whether_, _where_, and _how many times_ PR interaction happens — some work items never open a PR, others loop through review repeatedly — and keeps the engine agnostic about git-hosting concerns. A reader expecting the engine to "just open a PR when the run finishes" should know this was rejected in favour of graph control.

## Consequences

- While parked at the PR node, a background heartbeat poller (`PrStatusPoller`) polls real PR state and fires named custom edges on transitions: `on_rejected`, `on_merge_conflict`, `on_ci_failed`, `on_approved`, `on_ci_passed`, `on_merged`, `on_abandoned` (priority order). Only a **connected** edge routes the run, and only the single highest-priority state that newly became true that tick fires; unconnected states just refresh the persisted snapshot. See the **PR Heartbeat** entry in [CONTEXT.md](../../CONTEXT.md).
- There is **no fallback** to `on_success`/`on_failure` for any PR state. A PR that merges (or is abandoned) with no `on_merged`/`on_abandoned` edge wired leaves the run parked indefinitely — by design — so the graph author must wire those edges to reach a terminal/Cleanup path.
- A no-changes guard fails the node when the branch has zero commits ahead of the target, so an empty PR is never opened.
