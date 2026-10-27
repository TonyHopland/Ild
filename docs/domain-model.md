# Domain Model

| Concept                    | Meaning                                                                                                                   |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| `WorkItem`                 | Shared remote unit of work stored on the WorkItem Server                                                                  |
| `LoopTemplate`             | Named workflow definition with immutable saved versions                                                                   |
| `LoopRun`                  | One local execution of a template version against a work item                                                             |
| `LoopRunNode`              | One visited node execution within a run                                                                                   |
| `RemoteProvider`           | Git provider settings                                                                                                     |
| WorkItem Server connection | App-wide URL, API key, and poll/grace cadence for reaching the WorkItem Server (stored in app settings, not per provider) |
| `AiProvider`               | Adapter-resolved AI provider configuration                                                                                |
| `AiProviderTag`            | User-chosen label on at most one `AiProvider`; an AI node's `aiProviderTag` runs it there, else on the default provider   |
| `RecoveryPolicy`           | `AutoResume`, `NeedsReview`, or `Cancel` after restart                                                                    |
| `UserSession`              | One signed-in device. Distinct from a Chat Session (a transcript) and from an agent/adapter session                       |
| Active Work Item Set       | Work items behind this instance's live runs; derived per poll pass, and both the heartbeat list and the concurrency gate  |

## Node types

Loop templates are directed graphs built from these node types:

| Node      | Role                                                                              |
| --------- | --------------------------------------------------------------------------------- |
| `Start`   | Entry point; optionally creates a worktree and branch from a clean `origin` base  |
| `Cmd`     | Runs a shell command in the worktree (as the agent user under uid isolation)      |
| `AI`      | Delegates to an `IAgentAdapter` resolved by `AiProvider.Type`                     |
| `Human`   | Pauses for human input, which becomes `{{PreviousNode.Output}}` downstream        |
| `Prompt`  | Renders a templated prompt and emits it as output (composes prompts for AI nodes) |
| `SetVariable` | Writes loop variables from templated values and passes its input on unchanged |
| `PR`      | Creates or reuses a pull request and waits for merge/rejection webhooks           |
| `Cleanup` | Terminal sink node; ends the run, and its worktree and session data are removed  |

## AI execution model

AI nodes resolve an `IAgentAdapter` from the configured `AiProvider.Type`. The currently registered provider types are **`opencode`**, **`pi`**, **`claude-code`**, **`copilot`**, and **`codex`** — all are CLI-backed: the adapter spawns the provider's CLI inside the worktree and reads its structured output. The adapter, not the node executor, owns the provider-specific execution lifecycle, including multi-turn loops and session handling.

Each AI node has a single `prompt` field. When first-turn and follow-up prompts need to differ, model that explicitly with an upstream `Prompt` node.

## Key behaviors

- Work-item tags drive loop-template resolution; tags must match exactly one template (zero or multiple → HumanFeedback).
- Templates are versioned on every save; a run pins the version it started with, so editing a template mid-run does not disturb it. The next run re-resolves and may pick a newer version.
- Ready items can be claimed automatically by the poller or started manually from the UI.
- A human answer goes only to the work item's active run, and only while that run waits at a human or PR node; the run resumes and the item moves from `HumanFeedback` to `Running`. An answer naming any other run is refused. `WaitingForIld` is only for ILD's own waits, such as an AI provider at capacity.
- Each run gets its own worktree and branch (`ild/wi-<workItemId>-run-<runId>`). When the run ends (Completed or Cancelled), its worktree, local branch and AI session snapshots are removed once no loop drives it; the run row — node path, event log, feedback, PR snapshot — stays until the `WorktreeRetentionSweeper` deletes it, once the work item is Done and the run finished more than `run.retentionDays` days ago (default `14`), or a manual run delete. A Failed run keeps its worktree while its item is in play. The sweeper also removes whatever a finished run left behind. A run pinned with `Retain` keeps its worktree, branch, sessions and history until it is unpinned; the next sweep then removes its leftovers.
- A work item may also carry a `BaseBranchOverride`: the ref its runs branch from, and the branch their PRs target. Blank means the repository's default branch, which is the only base runs had before. It lets an item continue, review, or hotfix a branch. The value is pinned on the `LoopRun` at creation, so editing it only redirects the item's next run, and a run whose base is not on `origin` fails at the Start node rather than quietly falling back to the default. See [ADR-0008](./adr/0008-worktree-and-branch-per-run.md).
- A work item may carry a `BranchNameOverride`, which then **is** the branch name for every run of that item — verbatim, with no per-run suffix. Because the branch is no longer unique per run, the engine refuses to start a run on a name that any run row, local branch, or `origin` already holds: the item parks in `HumanFeedback` before any run row or worktree exists. A re-run of a custom-named item therefore parks while its predecessor's branch is still held — a pinned run, or a Failed one whose item is in play, keeps it, and retention never releases it while the item is not Done, so a human deletes that run or runs a Run Cleanup on it — and either releases the local branch only — ILD never deletes remote branches. See [ADR-0008](./adr/0008-worktree-and-branch-per-run.md).

- A user has as many `UserSession` rows as they have signed-in devices, so logging in on one never disturbs another and each can be signed out on its own. The bearer token is stored only as a hash — an HMAC-SHA256 keyed on the `ILD_SESSION_TOKEN_PEPPER` server secret where one is configured, an unkeyed SHA-256 otherwise — and rows are addressed by that hash; the plaintext exists solely in the client. Under a pepper, a party that can write the table but not read the pepper cannot produce a row that authenticates. Signing out stamps `RevokedAt` rather than deleting the row. A session dies when it is revoked, when it has gone unused for `session.idleDays`, or when it passes the `ExpiresAt` stamped from `session.maxDays` at sign-in.

For the full glossary, relationships, lifecycle states, and execution/recovery semantics, see [CONTEXT.md](../CONTEXT.md).
