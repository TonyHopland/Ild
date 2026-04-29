# ILD (In-Loop Development)

A containerized AI-assisted development platform that runs configurable workflow loops over work items, integrating git worktrees, AI agents, and pull request lifecycle management.

## Language

### Core Concepts

**WorkItem**:
A unit of work — code change, planning session, or any task type. May or may not involve a repository, worktree, or PR.
_Avoid_: task, ticket, issue, story

**LoopTemplate**:
A named, versioned workflow definition expressed as a directed graph of nodes (Start, Cmd, AI, Human, PR, Cleanup). Auto-versioned on every save.
_Avoid_: workflow, pipeline, template (without "loop")

**LoopTemplateVersion**:
An immutable snapshot of a LoopTemplate's graph at a point in time. Created on every save. WorkItems pin a version at first run start.
_Avoid_: template revision, template snapshot

**LoopRun**:
A single execution of a pinned LoopTemplateVersion against a WorkItem. Tracks node states, edge traversals, and timing.
_Avoid_: execution, run, instance

**Worktree**:
A per-WorkItem git worktree created on first run, destroyed when Cleanup executes. Each new run after cleanup creates a fresh worktree.
_Avoid_: workspace, checkout

### Node Types

**Start Node**:
The entry point of a loop graph. Optionally creates a worktree and branch.
_Avoid_: init, setup

**Cmd Node**:
Executes a shell command in the worktree with a configurable timeout. Succeeds on exit code 0.
_Avoid_: shell, command

**AI Node**:
Delegates execution to an `IAgentAdapter` resolved by `AiProvider.Type`. The adapter controls the full execution lifecycle including multi-turn loops, tool use, and internal state. Has two prompt fields: `initialPrompt` (first execution) and `loopPrompt` (subsequent loopback executions).
_Avoid_: LLM node, model node

**Human Node**:
A pause point that awaits human input. The input becomes `{{PreviousNode.Output}}` for downstream nodes. Can form conversational loops with AI nodes (e.g., grill-me planning).
_Avoid_: approval gate, review step

**PR Node**:
Creates a pull request (or reuses existing one via `WorkItem.PrUrl`). Waits for webhook events: merge routes to `on_success`, rejection routes to `on_failure`. Makes the PR lifecycle explicit in the graph.
_Avoid_: repository node, git node

**Cleanup Node**:
A terminal (sink) node with only incoming edges. Destroys the worktree when executed, ending the LoopRun.
_Avoid_: teardown, end node

### Execution & Recovery

**Agent Adapter**:
A pluggable component implementing `IAgentAdapter` that handles full AI node execution. Each adapter declares `SupportedProviderTypes` and is auto-registered via DI. Adapter instance is scoped per AI node per `LoopRun` — sibling AI nodes in the same loop do not share state. The OpenAI-compatible adapter is the default.
_Avoid_: LLM provider, model driver

**Tool Autonomy**:
Adapters have full autonomy over tool execution. Tools are declared by the adapter, not the engine. (Per-node tool allowlists are a future enhancement.)
_Avoid_: tool sandbox, tool permissions

**Event Log**:
Append-only audit stream per LoopRun. Serves as AI context and observability. Large payloads (>10KB) stored on disk; DB stores path reference. Retained for 7 days after successful completion; failed runs preserved until manually closed.
_Avoid_: audit trail, log

**Recovery Policy**:
Per-LoopTemplate setting controlling crash recovery behavior: AutoResume, NeedsReview, or Cancel. On recovery, in-flight nodes are re-executed.
_Avoid_: restart policy, failover

### Work Item Lifecycle

**Human Feedback**:
A single WorkItem status for all cases requiring human attention: PR awaiting merge, node failure exhaustion, rebase conflicts, Human node input. Distinguished by `HumanFeedbackReason` string.
_Avoid_: paused, blocked, stalled

**Ready**:
WorkItem status meaning all dependencies are Done and the work item can begin execution.
_Avoid_: available, queued

**Backlog**:
WorkItem status for items requiring human approval before entering the work queue. Whether new items land here or in Work Queue depends on a per-Repository setting.
_Avoid_: draft, planned

## Relationships

- A **WorkItem** has zero or more **LoopRun**s, but at most one active run at a time
- A **LoopRun** executes exactly one pinned **LoopTemplateVersion**. Status is `Running` until Cleanup executes, then `Completed`. PR merge/rejection is handled by a **PR Node** in the graph, not implicit.
- A **WorkItem** references a **LoopTemplate** directly. `LoopTemplateVersionId` is null ("Latest") until the first run starts, then pinned forever
- A **WorkItem** may have dependencies on other **WorkItem**s; it becomes ready when all dependencies reach `Done` status
- New **WorkItem**s land in either Backlog or Work Queue based on a per-**Repository** gating setting (Backlog = requires human approval to proceed, Work Queue = auto-flows)
- A **WorkItem** is optionally associated with a **Repository** (Plan-type items may not need one)
- A **Worktree** is created on first run, destroyed on Cleanup. Re-starting a WorkItem creates a fresh worktree
- `{{PreviousNode.Output}}` resolves to the source node of the incoming edge, not the chronologically previous execution
- `{{EventLog.LastN}}` defaults to N=10, capped at 50
- AI node uses `initialPrompt` on first execution, `loopPrompt` on loopback executions
- `AiProvider.Config` is a free-form JSON blob; each adapter reads what it needs
- Rebase happens only at loop start, not before each node
- Failed/cancelled WorkItems: "Done" destroys worktree and discards; "Backlog" fully resets for re-planning
- Safety net (max node execs + wall clock): finishes current node, then cancels the run

## Example Dialogue

> **Dev:** "When I create a work item, does it start running immediately?"
> **Domain expert:** "No — it lands in Backlog or Work Queue depending on the repository's gating setting. If it has no dependencies and lands in Work Queue, it auto-transitions to Ready. You still need to click Start."

> **Dev:** "Can I change the loop template for a running work item?"
> **Domain expert:** "No — the LoopTemplateVersion is pinned at first run start. Unstarted work items float to the latest version. Only after the first run does the version stay fixed."

> **Dev:** "What happens if the container crashes mid-loop?"
> **Domain expert:** "Recovery policy determines the action. With AutoResume, the in-flight node re-executes. Human node input is preserved. The safety net bounds how many times nodes can re-run."

## Flagged ambiguities

- "WorkItem" covers both code changes and non-code work (planning) — confirmed intentional. `WorktreePath` and `BranchName` are nullable to support this.
- Dependency readiness trigger is `Done` status (not `IsPrMerged`) — works for all WorkItem types
- "Latest" template version is resolved at first run start, not at WorkItem creation
- PR lifecycle is explicit in the graph via **PR Node**, not implicit engine behavior
- `WorkItem.Type` field is redundant — removed. Template assignment determines work type.
- Backlog vs Work Queue landing is per-Repository, not global or per-WorkItem
