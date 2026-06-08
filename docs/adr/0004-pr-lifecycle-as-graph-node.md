# ADR-0004: PR lifecycle is an explicit graph node, not implicit engine behavior

Creating a pull request, pushing the branch, waiting for merge/rejection webhooks, and routing on the outcome are modelled as a first-class **PR node** in the loop graph rather than baked into the engine as automatic end-of-run behavior. Making it explicit lets a template decide _whether_, _where_, and _how many times_ PR interaction happens — some work items never open a PR, others loop through review repeatedly — and keeps the engine agnostic about git-hosting concerns. A reader expecting the engine to "just open a PR when the run finishes" should know this was rejected in favour of graph control.

## Consequences

- Merge routes to `on_success`, rejection to `on_failure`; the graph author owns what happens next.
- A no-changes guard fails the node when the branch has zero commits ahead of the target, so an empty PR is never opened.
