# ADR-0006: Every run starts from a clean `origin/<default>` base

The Start node fetches and hard-resets the base repository to `origin/<defaultBranch>` and rebases the run's branch onto it before any work begins. Reset failure and rebase failure both **fail the node** — they are not best-effort. We enforce this because review and PR steps go wrong when state from a prior run leaks into the next one; a guaranteed clean origin base is the invariant that makes runs reproducible and independent.

## Consequences

- Local-only changes in the base repo are discarded on every run by design — the base repo is treated as a disposable mirror of origin, not a working copy.
- A run cannot proceed on a stale or diverged base; the node fails loudly rather than silently building on old state.
- `git fetch` itself is best-effort (a transient fetch failure is swallowed), but the subsequent reset is not — see the Start node entry in [CONTEXT.md](../../CONTEXT.md).
