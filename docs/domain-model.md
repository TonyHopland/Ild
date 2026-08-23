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
| `RecoveryPolicy`           | `AutoResume`, `NeedsReview`, or `Cancel` after restart                                                                    |
| `UserSession`              | One signed-in device. Distinct from a Chat Session (a transcript) and from an agent/adapter session                       |
| Active Work Item Set       | Work items behind this instance's live runs; derived per poll pass, and both the heartbeat list and the concurrency gate  |

## Node types

Loop templates are directed graphs built from these node types:

| Node      | Role                                                                              |
| --------- | --------------------------------------------------------------------------------- |
| `Start`   | Entry point; optionally creates a worktree and branch from a clean `origin` base  |
| `Cmd`     | Runs a shell command in the worktree                                              |
| `AI`      | Delegates to an `IAgentAdapter` resolved by `AiProvider.Type`                     |
| `Human`   | Pauses for human input, which becomes `{{PreviousNode.Output}}` downstream        |
| `Prompt`  | Renders a templated prompt and emits it as output (composes prompts for AI nodes) |
| `PR`      | Creates or reuses a pull request and waits for merge/rejection webhooks           |
| `Cleanup` | Terminal sink node; ends the run, keeping its worktree and branch for inspection  |

## AI execution model

AI nodes resolve an `IAgentAdapter` from the configured `AiProvider.Type`. The currently registered provider types are **`opencode`**, **`pi`**, **`claude-code`**, and **`copilot`** — all are CLI-backed: the adapter spawns the provider's CLI inside the worktree and reads its structured output. The adapter, not the node executor, owns the provider-specific execution lifecycle, including multi-turn loops and session handling.

Each AI node has a single `prompt` field. When first-turn and follow-up prompts need to differ, model that explicitly with an upstream `Prompt` node.

## Key behaviors

- Work-item tags drive loop-template resolution; tags must match exactly one template (zero or multiple → HumanFeedback).
- Templates are versioned on every save; a run pins the version it started with, so editing a template mid-run does not disturb it. The next run re-resolves and may pick a newer version.
- Ready items can be claimed automatically by the poller or started manually from the UI.
- Human feedback moves remote items through `HumanFeedback` and `WaitingForIld` before resuming execution.
- Each run gets its own worktree and branch (`ild/wi-<workItemId>-run-<runId>`), kept after the run finishes for inspection. They are destroyed only when the run itself is deleted — by the `WorktreeRetentionSweeper` after `run.retentionDays`, or a manual run delete (runs pinned with `Retain` are never auto-deleted).
- A work item may also carry a `BaseBranchOverride`: the ref its runs branch from, and the branch their PRs target. Blank means the repository's default branch, which is the only base runs had before. It lets an item continue, review, or hotfix a branch. The value is pinned on the `LoopRun` at creation, so editing it only redirects the item's next run, and a run whose base is not on `origin` fails at the Start node rather than quietly falling back to the default. See [ADR-0008](./adr/0008-worktree-and-branch-per-run.md).
- A work item may carry a `BranchNameOverride`, which then **is** the branch name for every run of that item — verbatim, with no per-run suffix. Because the branch is no longer unique per run, the engine refuses to start a run on a name that any run row, local branch, or `origin` already holds: the item parks in `HumanFeedback` before any run row or worktree exists. A re-run of a custom-named item therefore parks until its predecessor's branch is released, and deleting that run releases the local branch only — ILD never deletes remote branches. See [ADR-0008](./adr/0008-worktree-and-branch-per-run.md).

- A user has as many `UserSession` rows as they have signed-in devices, so logging in on one never disturbs another and each can be signed out on its own. The bearer token is stored only as a hash keyed on the `ILD_SESSION_TOKEN_PEPPER` server secret, and rows are addressed by that hash; the plaintext exists solely in the client. A party that can write the table but not read the pepper cannot produce a row that authenticates. Signing out stamps `RevokedAt` rather than deleting the row. A session dies when it is revoked, when it has gone unused for `session.idleDays`, or when it passes the `ExpiresAt` stamped from `session.maxDays` at sign-in.

For the full glossary, relationships, lifecycle states, and execution/recovery semantics, see [CONTEXT.md](../CONTEXT.md).
