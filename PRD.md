# ILD (In-Loop Development) — Product Requirements Document

## Problem Statement

Software development workflows that involve coding, testing, reviewing, and merging are repetitive and context-heavy. Developers switch between tools, lose context between iterations, and manually coordinate the loop between writing code, running tests, getting reviews, and addressing feedback. There is no system that combines task tracking, configurable workflow automation, AI-assisted development, and repository management into a single cohesive tool that runs in a container and manages the full lifecycle from work item to merged pull request.

## Solution

ILD is a containerized AI-assisted development platform that provides:

- A **taskboard** for creating and tracking work items with dependency management
- **Configurable workflow loops** — directed graphs of nodes (Cmd, AI, Human, PR) that execute autonomously
- **Repository integration** — automatic worktree management, branch creation, and pull request lifecycle
- **AI agent orchestration** — LLM-powered nodes with configurable prompts, in-process tool access, and multi-provider support
- **Human-in-the-loop** — pause points for review, approval, and context injection at any time

The system runs in a single container, persists state in SQLite, and integrates with git providers (Forgejo, GitHub, GitLab) for pull request management.

## User Stories

### Project Setup

1. As a developer, I want to deploy ILD as a single container, so that I can get started quickly without complex infrastructure
2. As a developer, I want to configure my git provider connection (Forgejo/GitHub/GitLab), so that ILD can create and manage pull requests
3. As a developer, I want to register repositories with ILD, so that work items can be associated with specific codebases
4. As a developer, I want to configure LLM providers with API keys, so that AI nodes can make model calls
5. As a developer, I want to mount a host directory for worktrees, so that my work persists across container restarts
6. As a developer, I want to set up basic authentication via environment variables, so that unauthorized users cannot access my ILD instance

### Work Item Management

7. As a developer, I want to create a work item with a title and description, so that I can define a unit of work
8. As a developer, I want to assign a loop template to a work item, so that the work follows a defined workflow
9. As a developer, I want to assign a repository to a work item, so that the loop operates on the correct codebase
10. As a developer, I want to set dependencies between work items, so that work items only become ready when their dependencies reach Done
11. As a developer, I want to see work items that depend on mine, so that I understand the impact of my work
12. As a developer, I want circular dependencies to be rejected, so that I don't create deadlocked work items
13. As a developer, I want to view a work item's details in a modal, so that I can see all relevant information without leaving the board
14. As a developer, I want to tag work items with labels, so that I can categorize and filter them
15. As a developer, I want to set priority on work items, so that important work is visible
16. As a developer, I want unstarted work items to use the latest loop template version, so that they benefit from template improvements
17. As a developer, I want started work items to keep their original template version, so that mid-run template changes don't break execution

### Taskboard

18. As a developer, I want to see work items organized by status columns (Backlog, Work Queue, Ready, Running, Human Feedback, Done), so that I can see the state of all work at a glance
19. As a developer, I want work items in Work Queue to automatically transition to Ready when all dependencies reach Done, so that I know when work can begin
20. As a developer, I want work items with no dependencies in Work Queue to immediately transition to Ready, so that independent work flows without delay
21. As a developer, I want to click a work item to open its details modal, so that I can inspect and start it
22. As a developer, I want to click a Start button in the work item modal, so that I can begin a loop run
23. As a developer, I want to see clickable dependency links in the work item modal, so that I can navigate to dependent work items
24. As a developer, I want to see loop run history in the work item modal, so that I can review past executions
25. As a developer, I want to see the pull request link when one exists, so that I can review the changes
26. As a developer, I want to manually link an externally-created PR to a work item, so that I can bypass webhook dependency
27. As a developer, I want to manually mark a PR as merged, so that I can trigger state transitions when webhooks are unavailable

### Loop Template Design

28. As a developer, I want to create a loop template using a visual node editor (React Flow), so that I can design workflows without writing code
29. As a developer, I want to drag and drop nodes onto a canvas and connect them, so that I can visually design the workflow
30. As a developer, I want to configure each node's settings in a side panel, so that I can customize behavior per node
31. As a developer, I want to configure edge traversal limits on the source node, so that I can control how many times a path is taken
32. As a developer, I want loops to support cycles (back-edges), so that I can create iterative workflows like code-review-code
33. As a developer, I want every loop to have a Start node and a Cleanup node, so that setup and teardown are explicit
34. As a developer, I want the Start node to optionally create a worktree, so that I can have planning loops that don't touch the repository
35. As a developer, I want the Cleanup node to destroy the worktree, so that resources are freed after the run completes
36. As a developer, I want loop templates to be auto-versioned on every save, so that I can track how templates evolve
37. As a developer, I want to clone a loop template to create a variant, so that I can reuse workflows with modifications
38. As a developer, I want validation on save that checks: Start node exists, Cleanup node exists, all nodes are reachable, and at least one path leads to Cleanup
39. As a developer, I want to see seed loop templates on first use, so that I have starting points to work from
40. As a developer, I want prompt template validation that flags unknown placeholders, so that I know my prompts are correct

### Node Types

41. As a developer, I want Cmd nodes that execute shell commands in the worktree, so that I can run builds, tests, and linting
42. As a developer, I want Cmd nodes to have configurable timeouts, so that long-running commands don't hang the loop
43. As a developer, I want Cmd nodes to succeed on exit code 0, so that standard Unix conventions apply
44. As a developer, I want Cmd node stdout/stderr captured as structured output, so that I can review command output
45. As a developer, I want AI nodes that trigger LLM calls with configurable prompt templates, so that AI can assist with coding and review
46. As a developer, I want prompt templates to support placeholders (work item title, description, event log, diff, file contents, previous node output), so that I can customize AI behavior
47. As a developer, I want AI nodes to have access to in-process tools (git, file read/write, command execution), so that AI can make changes to the codebase
48. As a developer, I want tool access to be configurable per node, so that not every AI node has write access
49. As a developer, I want AI nodes to have access to ILD API tools (create work item, read work item, list loop templates), so that AI can decompose planning into child work items
50. As a developer, I want Human nodes that pause execution and await input, so that I can review and approve work
51. As a developer, I want Human nodes to have Continue and Reject options, so that I can approve work or send it back for revision
52. As a developer, I want to add messages to the event log at any time, so that I can provide context mid-execution
53. As a developer, I want to pause a running loop mid-execution, so that I can intervene when needed

### Loop Execution

54. As a developer, I want to see real-time updates of loop execution in the UI, so that I can monitor progress
55. As a developer, I want to see which node is currently running, which have completed, and which are pending, so that I understand execution state
56. As a developer, I want failed nodes with an error edge to immediately follow the error edge, so that failure paths (e.g., back to AI coder) are routed explicitly
57. As a developer, I want failed nodes without an error edge to auto-retry up to N times, so that transient failures are handled gracefully
58. As a developer, I want the cycle counter to reset when a human triggers a retry, so that my intervention resets the iteration budget
59. As a developer, I want to view the event log for a loop run, so that I can trace what happened
60. As a developer, I want the event log to show AI context used for each AI node, so that I can debug AI behavior
61. As a developer, I want to pull/rebase a worktree at loop start, so that parallel work items don't diverge from main
62. As a developer, I want rebase conflicts to route to a Human node, so that I can resolve them manually
63. As a developer, I want the loop to check for pause signals before starting each node, so that I can interrupt execution
64. As a developer, I want running commands to receive cancellation tokens, so that paused execution stops gracefully

### Human Feedback State

65. As a developer, I want work items to transition to Human Feedback when waiting for PR merge, node failure exhaustion, rebase conflicts, or Human node input, so that all human-required attention is in one place
66. As a developer, I want visual badges in Human Feedback indicating the reason (PR awaiting merge, node failed, rebase conflict, etc.), so that I can prioritize
67. As a developer, I want to receive browser notifications when a work item enters Human Feedback, so that I'm alerted even when the tab isn't in focus
68. As a developer, I want to choose "Cleanup → Done" or "Cleanup → Backlog" for failed/cancelled work items, so that I control whether to discard or re-plan

### Pull Request Workflow

69. As a developer, I want a pull request to be created after the code is complete, so that reviewers can see the changes
70. As a developer, I want the AI to write the PR title and description, so that PRs are well-documented
71. As a developer, I want commits to be made by AI nodes via in-process tools, so that commit messages are meaningful
72. As a developer, I want to merge pull requests manually through Forgejo, so that I have final approval control
73. As a developer, I want PR comments from Forgejo to appear in the event log, so that feedback is captured in the loop context
74. As a developer, I want webhook-based comment sync from Forgejo, so that comments appear in real time
75. As a developer, I want the branch to be deleted after merge, so that the repository stays clean
76. As a developer, I want the work item to transition to Done when Cleanup executes, so that the board reflects reality

### Crash Recovery

77. As a developer, I want loop runs to recover after a container restart, so that work is not lost
78. As a developer, I want the in-flight node to be re-executed on recovery, so that incomplete work resumes correctly
79. As a developer, I want partial input in Human nodes to be preserved across restarts, so that I don't lose my review comments
80. As a developer, I want worktree health to be validated on recovery, so that corrupted worktrees are detected
81. As a developer, I want to configure recovery policy per loop template (auto-resume, needs review, cancel), so that I control risk per workflow

### Retention & Cleanup

82. As a developer, I want to configure event log retention period, so that storage doesn't grow unbounded
83. As a developer, I want failed runs to retain their event logs and worktrees until manually closed, so that I can investigate failures
84. As a developer, I want successful runs' event logs to be cleaned up after the retention period, so that storage is managed

### Observability

85. As a developer, I want structured JSON logs, so that I can parse and filter logs in the container environment
86. As a developer, I want to configure log level at runtime through the UI, so that I can debug without restarting
87. As a developer, I want git command output logged, so that I can debug worktree issues
88. As a developer, I want LLM API call details logged (latency, token usage), so that I can track costs
89. As a developer, I want tool invocations logged, so that I can audit AI actions
90. As a developer, I want a health endpoint that checks database, disk space, and remote provider connectivity, so that I can monitor system health
91. As a developer, I want an optional Prometheus metrics endpoint, so that I can integrate with monitoring systems

### Authentication

92. As a developer, I want to log in with username and password from environment variables, so that access to ILD is controlled
93. As a developer, I want API keys to be stored securely, so that credentials are not exposed
94. As a developer, I want sessions to persist across page reloads, so that I don't need to re-authenticate

## Implementation Decisions

### Resolved Design Decisions

| Decision              | Outcome                                                                                           |
| --------------------- | ------------------------------------------------------------------------------------------------- |
| Node execution model  | Sequential, single-threaded per loop run                                                          |
| Worktree lifecycle    | One worktree per work item, created on first run, destroyed when Cleanup node executes            |
| State model           | DB (`LoopRun`, `LoopRunNode` tables) is source of truth; event log is observability + AI context  |
| Tool architecture     | In-process tool abstractions (not MCP subprocesses)                                               |
| Agent adapter model   | `IAgentAdapter` resolved by `AiProvider.Type`; adapter controls full execution lifecycle          |
| Adapter lifecycle     | Per AI node per `LoopRun`; sibling AI nodes do not share state                                    |
| AI node prompts       | Single `prompt`; use explicit `Prompt` nodes before AI nodes when turn-specific wording is needed |
| Provider config       | Free-form JSON `Config` column on `AiProvider`; adapter reads what it needs                       |
| HIL pattern           | Graph-level only (AI → Human → loopback edge); adapter has no HIL awareness                       |
| Engine scheduling     | Async `while` loop with `CancellationToken`; SignalR pushes per node transition                   |
| Crash recovery        | Re-execute in-flight node; per-template policy (auto-resume / needs review / cancel)              |
| Node editor           | React Flow                                                                                        |
| Dependency resolution | Push-based via webhook on Done status                                                             |
| SignalR reconnect     | State snapshot + delta (not full event replay)                                                    |
| Prompt rendering      | Engine resolves placeholders; unknown placeholder validation on save                              |
| Large payloads        | Stored on disk under `data/payloads/{runId}/{sequence}.json`; DB stores path reference            |
| Webhook fallback      | Webhook-only; manual PR link/merge override available                                             |
| DB migrations         | EF Core auto-migrate on startup                                                                   |
| Auth                  | Single user, env vars only (`ILD_USERNAME`, `ILD_PASSWORD`), no wizard                            |
| Edge traversal limits | 200 node executions + 24h wall-clock default; configurable per template                           |
| Frontend              | Vite+ + React + TypeScript                                                                        |
| Retry semantics       | Error edge exists → follow immediately on failure; no error edge → auto-retry N times             |
| Edge types            | `on_success` / `on_failure`; max one of each per node                                             |
| Node output           | Structured output per node; source node of incoming edge is `{{PreviousNode.Output}}`             |
| Cleanup behavior      | Done → destroy worktree + branch; Failed/Cancelled → user chooses "Done" or "Backlog"             |
| Work item states      | `Backlog → Work Queue → Ready → Running → Human Feedback → Done`                                  |

### Module Architecture

The system is organized into the following deep modules:

1. **ILD.Data** — EF Core data layer: `AppDbContext`, entity models, DTOs, enums, and store interfaces (`IWorkItemStore`, `ILoopRunStore`, `ILoopTemplateStore`, etc.). Isolated from business logic so `ILD.Core` has no direct EF dependency.
2. **Loop Engine** — Core orchestration: sequential async `while` loop that executes nodes, evaluates `on_success`/`on_failure` edges, counts edge traversals, manages pause/resume via `CancellationToken`, handles retry logic (error edge immediate vs auto-retry N times)
3. **Event Log** — Append-only event store with monotonic sequence numbers, retention policy enforcement; serves as AI context and observability (not source of truth — DB is)
4. **Remote Provider** — Pluggable interface (`IRemoteProvider`) for git providers (Forgejo first, extensible to GitHub/GitLab). Handles PR creation, webhook registration, comment polling
5. **Repository Manager** — Worktree lifecycle (one per work item, create/destroy/validate), branch management, git operations (pull, rebase, commit, push)
6. **Agent Adapter** — Pluggable `IAgentAdapter` implementations resolved by `AiProvider.Type`. Each adapter controls full AI node execution lifecycle (multi-turn loops, tool use, internal state). Auto-registered via DI. Adapter instance scoped per AI node per `LoopRun`. Default adapter is OpenAI-compatible.
7. **Workitem Manager** — Workitem lifecycle, dependency graph with cycle detection, state machine transitions (`Backlog → Work Queue → Ready → Running → Human Feedback → Done`), readiness evaluation
8. **Loop Template Manager** — Template CRUD, auto-versioning on save, validation (reachability, required nodes), cloning
9. **PR Sync Service** — Webhook ingestion from git providers, comment-to-event translation, merge state detection
10. **Auth Service** — Single-user username/password from env vars, session management, API key storage
11. **Recovery Manager** — Crash recovery orchestration, re-execute in-flight node, worktree health validation, per-template recovery policy

### Stack

- **Backend**: .NET with a three-project split: `ILD.Data` (EF Core + SQLite), `ILD.Core` (business services), `ILD.Api` (ASP.NET Core host). Auto-migrate on startup.
- **Frontend**: Vite+ + React + TypeScript SPA
- **Real-time**: SignalR (WebSockets) with hub-based channels; state snapshot on reconnect (not full event replay)
- **Auth**: Single user, env vars (`ILD_USERNAME`, `ILD_PASSWORD`), PBKDF2 password hashing, server-side session
- **Logging**: Serilog with structured JSON output, runtime log level control

### Database Schema

Core tables: `RemoteProvider`, `Repository`, `LoopTemplate`, `LoopTemplateVersion`, `LoopNode`, `LoopNodeEdge`, `WorkItem`, `WorkItemDependency`, `LoopRun`, `LoopRunNode`, `LoopRunEdgeTraversal`, `EventLog`, `AiProvider`, `User`

Loop templates are normalized relational model. Node config is JSON per node type. Templates auto-version on save; workitems pin version at run start.

### Loop Execution Model

- General directed graph (cycles allowed), not a DAG
- Per-edge max traversals configured on source node, plus global safety net (200 node executions + 24h wall-clock default, configurable per template)
- DB-backed state: `LoopRun` and `LoopRunNode` tables are source of truth; `EventLog` is append-only for AI context and observability
- Node states: `Pending` → `Running` → `Succeeded`/`Failed`/`Skipped`/`WaitingHuman`
- Edge types: `on_success` and `on_failure` (max one each per node)
- Retry semantics: if `on_failure` edge exists → follow immediately on first failure; if no `on_failure` edge → auto-retry up to configured N times, then transition to Human Feedback
- Per-node retry count; human retry resets cycle counter
- Cooperative pause: engine checks `IsPaused` flag before each node; running commands receive cancellation tokens
- Sequential execution: one node at a time per loop run (worktree is shared mutable resource)

### AI Node Model

- One prompt field per AI node: `prompt`. If the first turn should differ from later turns, add `Prompt` nodes before the AI node instead of encoding two prompt fields into the AI node.
- Prompt templates with placeholders: `{{WorkItem.Title}}`, `{{WorkItem.Description}}`, `{{EventLog.LastN}}`, `{{EventLog.Summary}}`, `{{Node.Input}}`, `{{WorkTree.Diff}}`, `{{WorkTree.File:path}}`, `{{PreviousNode.Output}}`
- Unknown placeholder validation on template save
- `AINodeExecutor` resolves `IAgentAdapter` via `IAgentAdapterRegistry` based on `AiProvider.Type`
- Adapter controls full execution lifecycle: multi-turn LLM calls, tool use, internal state management
- Adapter instance scoped per AI node per `LoopRun` — sibling AI nodes do not share state
- Default adapter is OpenAI-compatible (`/chat/completions`). New adapters (Opencode, Pi, etc.) auto-register via DI
- `AiProvider.Config` is a free-form JSON blob; each adapter reads what it needs
- Tools are declared by the adapter (full autonomy). Per-node tool allowlists are a future enhancement
- Every node produces structured output; `{{PreviousNode.Output}}` resolves to the source node of the incoming edge

### Work Item State Machine

```
Backlog → Work Queue → Ready → Running → Human Feedback → Done
                                    ↓                  ↓
                              (auto-retry N)      (cleanup → Done or Backlog)
```

- Items with no dependencies auto-transition from Work Queue to Ready immediately
- Items with dependencies transition from Work Queue to Ready when all deps reach Done (push via webhook)
- `Running` → `Human Feedback` when: node retries exhausted, PR awaiting merge, rebase conflict, Human node awaiting input
- `Human Feedback` → `Running` on retry, or → `Done` on merge/cleanup
- Failed/Cancelled items: user chooses "Cleanup → Done" (discard) or "Cleanup → Backlog" (reset, destroy worktree, clear run state, allow re-editing)
- Loops transition to `Done` when Cleanup executes. Code-change items use a PR node before Cleanup.

### PR Workflow

- PR node in the loop graph creates the PR (or reuses existing one) and waits for webhook events
- Merge routes to `on_success` (toward Cleanup), rejection routes to `on_failure` (back to AI coder)
- Commits made by AI nodes via in-process tools
- Human merge only (v1)
- Forgejo webhooks push PR comments to ILD event log (webhook-only, no polling)
- Manual PR link/merge override available as fallback when webhooks are unavailable
- Squash merge on merge, branch deleted post-merge

### Deployment

- Single container image, worktrees on host-mounted volume
- Built-in Kestrel web server serving SPA static files
- Seed loop templates on first run:
  1. **Simple Code Change** — Start → Cmd (build) → Cmd (test) → Cleanup
  2. **AI-Assisted Feature** — Start → AI (implement) → Cmd (build) → Cmd (test) → Human (review) → edge back to AI if rejected, to PR creation if approved → Cleanup
  3. **Plan** — Start (no worktree) → AI (plan) → Human (approve) → AI (create work items) → Cleanup
- Configurable event log retention; failed runs retained until manually closed
- Large payloads (>10KB) stored on disk under `data/payloads/{runId}/{sequence}.json`; periodic cleanup of orphaned files

### API

- RESTful API, versioned from day one (`/api/v1/...`)
- Cursor-based pagination for event logs
- Large payloads (>10KB) stored on disk, database stores path reference
- SignalR hubs: `LoopRunHub` (node state + events), `WorkItemHub` (state changes)
- Frontend dev proxy forwards `/api` to backend during development

## Testing Decisions

### What Makes a Good Test

Tests should verify external behavior and observable outcomes, not internal implementation details. Each test should:

- Exercise a module through its public interface
- Use mocks/fakes for external dependencies (database, filesystem, LLM providers, git providers)
- Verify state transitions, event sequences, and business rules
- Be deterministic and fast (no real network calls, no real git operations)

### Modules With Tests

1. **Loop Engine** — Test sequential node execution, `on_success`/`on_failure` edge routing, edge traversal counting, cycle detection, pause/resume behavior, retry logic (error edge immediate vs auto-retry), recovery policy enforcement, global safety net (200 execs + 24h). Mock the event log, node executors, and worktree manager.
2. **Event Log** — Test append-only semantics, sequence numbering, retention policy enforcement (deletion after retention period, failed run preservation). Mock the storage layer.
3. **Workitem Manager** — Test dependency graph validation (cycle detection), state machine transitions (Backlog → Work Queue → Ready → Running → Human Feedback → Done), readiness evaluation (all deps merged via webhook push), workitem lifecycle. Mock the database and loop engine.
4. **Loop Template Manager** — Test auto-versioning, validation rules (start/cleanup required, reachability), cloning, version pinning semantics. Mock the database.
5. **Auth Service** — Test login/logout, session lifecycle, password hashing, API key storage. Mock the user store.
6. **Remote Provider** — Test PR creation payload, webhook registration, comment parsing. Mock HTTP calls to the git provider API.
7. **Agent Adapter** — Test prompt template rendering with all placeholders, unknown placeholder validation, provider selection (per-node vs global default), adapter delegation, adapter-declared tool execution. Mock the adapter and tools.

### Prior Art

These modules follow the pattern of testing business logic through interfaces with mocked infrastructure. The Loop Engine tests are analogous to workflow engine tests in systems like Temporal or Cadence. The Workitem Manager dependency tests are analogous to package manager dependency resolution tests.

## Out of Scope

- Bidirectional PR comment sync (ILD → Forgejo posting comments) — v1 is one-way only
- Auto-merge — merge is manual through Forgejo
- OAuth authentication — simple username/password only
- Mobile client — web UI only
- Multi-tenant support — single user/instance
- Real-time collaboration — single user editing at a time
- AI agent sandboxing beyond container boundary — container is the sandbox
- Performance optimization for large-scale deployments — designed for single-user container deployment
- GraphQL API — REST only
- Native desktop application
- Plugin marketplace for custom node types — node types are built-in

## Further Notes

- The system is designed for a single developer running it in a container for personal AI-assisted development
- Forgejo is the primary git provider target, but the `IRemoteProvider` interface enables GitHub and GitLab support
- The event log is the source of truth for AI context — keeping it clean and well-structured is important
- Template versioning is critical: started workitems must not break if the template changes
- Recovery is a first-class concern: the system must survive container restarts gracefully
- The visual loop editor (React Flow) is a key UX surface — investing in good custom node rendering and edge UI is important
- Tool access should be conservative by default — read-only unless explicitly granted
- Worktree is one per work item, destroyed when Cleanup executes — each new run creates a fresh worktree
- Vite+ is alpha; keep frontend dependencies minimal and be prepared for potential breaking changes
