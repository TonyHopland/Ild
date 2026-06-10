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
| `ActiveWorkItemTracker`    | Local set of items this ILD instance is actively heartbeating                                                             |

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

AI nodes resolve an `IAgentAdapter` from the configured `AiProvider.Type`. The currently registered provider types are **`opencode`**, **`pi`**, and **`claude-code`** — all three are CLI-backed: the adapter spawns the provider's CLI inside the worktree and reads its structured output. The adapter, not the node executor, owns the provider-specific execution lifecycle, including multi-turn loops and session handling.

Each AI node has a single `prompt` field. When first-turn and follow-up prompts need to differ, model that explicitly with an upstream `Prompt` node.

## Key behaviors

- Work-item tags drive loop-template resolution; tags must match exactly one template (zero or multiple → HumanFeedback).
- Templates are versioned on every save; a run pins the version it started with, so editing a template mid-run does not disturb it. The next run re-resolves and may pick a newer version.
- Ready items can be claimed automatically by the poller or started manually from the UI.
- Human feedback moves remote items through `HumanFeedback` and `WaitingForIld` before resuming execution.
- Each run gets its own worktree and branch (`ild/wi-<workItemId>-run-<runId>`), kept after the run finishes for inspection. They are destroyed only when the run itself is deleted — by the `WorktreeRetentionSweeper` after `run.retentionDays`, or a manual run delete (runs pinned with `Retain` are never auto-deleted).

For the full glossary, relationships, lifecycle states, and execution/recovery semantics, see [CONTEXT.md](../CONTEXT.md).
