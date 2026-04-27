# ILD (In-Loop Development) — Product Requirements Document

## Problem Statement

Software development workflows that involve coding, testing, reviewing, and merging are repetitive and context-heavy. Developers switch between tools, lose context between iterations, and manually coordinate the loop between writing code, running tests, getting reviews, and addressing feedback. There is no system that combines task tracking, configurable workflow automation, AI-assisted development, and repository management into a single cohesive tool that runs in a container and manages the full lifecycle from work item to merged pull request.

## Solution

ILD is a containerized AI-assisted development platform that provides:

- A **taskboard** for creating and tracking work items with dependency management
- **Configurable workflow loops** — directed graphs of nodes (command, AI, human) that execute autonomously
- **Repository integration** — automatic worktree management, branch creation, and pull request lifecycle
- **AI agent orchestration** — LLM-powered nodes with configurable prompts, tool access via MCP, and multi-provider support
- **Human-in-the-loop** — pause points for review, approval, and context injection at any time

The system runs in a single container, persists state in SQLite, and integrates with git providers (Forgejo, GitHub, GitLab) for pull request management.

## User Stories

### Project Setup

1. As a developer, I want to deploy ILD as a single container, so that I can get started quickly without complex infrastructure
2. As a developer, I want to configure my git provider connection (Forgejo/GitHub/GitLab), so that ILD can create and manage pull requests
3. As a developer, I want to register repositories with ILD, so that work items can be associated with specific codebases
4. As a developer, I want to configure LLM providers with API keys, so that AI nodes can make model calls
5. As a developer, I want to mount a host directory for worktrees, so that my work persists across container restarts
6. As a developer, I want to set up basic authentication, so that unauthorized users cannot access my ILD instance

### Work Item Management

7. As a developer, I want to create a work item with a title and description, so that I can define a unit of work
8. As a developer, I want to assign a loop template to a work item, so that the work follows a defined workflow
9. As a developer, I want to assign a repository to a work item, so that the loop operates on the correct codebase
10. As a developer, I want to set dependencies between work items, so that work items only become ready when their dependencies are merged
11. As a developer, I want to see work items that depend on mine, so that I understand the impact of my work
12. As a developer, I want circular dependencies to be rejected, so that I don't create deadlocked work items
13. As a developer, I want to view a work item's details in a modal, so that I can see all relevant information without leaving the board
14. As a developer, I want to tag work items with labels, so that I can categorize and filter them
15. As a developer, I want to set priority on work items, so that important work is visible
16. As a developer, I want unstarted work items to use the latest loop template version, so that they benefit from template improvements
17. As a developer, I want started work items to keep their original template version, so that mid-run template changes don't break execution

### Taskboard

18. As a developer, I want to see work items organized by status columns (Backlog, Ready, Running, PR Open, Merged, Failed), so that I can see the state of all work at a glance
19. As a developer, I want work items to automatically transition to Ready when all dependencies are merged, so that I know when work can begin
20. As a developer, I want to click a work item to open its details modal, so that I can inspect and start it
21. As a developer, I want to click a Start button in the work item modal, so that I can begin a loop run
22. As a developer, I want to see clickable dependency links in the work item modal, so that I can navigate to dependent work items
23. As a developer, I want to see loop run history in the work item modal, so that I can review past executions
24. As a developer, I want to see the pull request link when one exists, so that I can review the changes

### Loop Template Design

25. As a developer, I want to create a loop template using a visual node editor, so that I can design workflows without writing code
26. As a developer, I want to drag and drop nodes onto a canvas and connect them, so that I can visually design the workflow
27. As a developer, I want to configure each node's settings in a side panel, so that I can customize behavior per node
28. As a developer, I want to configure edge traversal limits on the source node, so that I can control how many times a path is taken
29. As a developer, I want loops to support cycles (back-edges), so that I can create iterative workflows like code-review-code
30. As a developer, I want every loop to have a Start node and a Cleanup node, so that setup and teardown are explicit
31. As a developer, I want the Start node to optionally create a worktree, so that I can have planning loops that don't touch the repository
32. As a developer, I want the Cleanup node to destroy the worktree, so that resources are freed after the run completes
33. As a developer, I want loop templates to be auto-versioned on every save, so that I can track how templates evolve
34. As a developer, I want to clone a loop template to create a variant, so that I can reuse workflows with modifications
35. As a developer, I want validation on save that checks: Start node exists, Cleanup node exists, all nodes are reachable, and at least one path leads to Cleanup
36. As a developer, I want to see seed loop templates on first use, so that I have starting points to work from

### Node Types

37. As a developer, I want Cmd nodes that execute shell commands in the worktree, so that I can run builds, tests, and linting
38. As a developer, I want Cmd nodes to have configurable timeouts, so that long-running commands don't hang the loop
39. As a developer, I want Cmd nodes to succeed on exit code 0, so that standard Unix conventions apply
40. As a developer, I want Cmd node stdout/stderr captured as events, so that I can review command output
41. As a developer, I want AI nodes that trigger LLM calls with configurable prompt templates, so that AI can assist with coding and review
42. As a developer, I want prompt templates to support placeholders (work item title, description, event log, diff, file contents, previous node output), so that I can customize AI behavior
43. As a developer, I want AI nodes to have access to MCP tools (git, file read/write, command execution), so that AI can make changes to the codebase
44. As a developer, I want tool access to be configurable per node, so that not every AI node has write access
45. As a developer, I want Human nodes that pause execution and await input, so that I can review and approve work
46. As a developer, I want Human nodes to have Continue and Reject options, so that I can approve work or send it back for revision
47. As a developer, I want to add messages to the event log at any time, so that I can provide context mid-execution
48. As a developer, I want to pause a running loop mid-execution, so that I can intervene when needed

### Loop Execution

49. As a developer, I want to see real-time updates of loop execution in the UI, so that I can monitor progress
50. As a developer, I want to see which node is currently running, which have completed, and which are pending, so that I understand execution state
51. As a developer, I want failed nodes to be retryable with a configurable retry count, so that transient failures don't kill the run
52. As a developer, I want the cycle counter to reset when a human triggers a retry, so that my intervention resets the iteration budget
53. As a developer, I want to view the event log for a loop run, so that I can trace what happened
54. As a developer, I want the event log to show AI context used for each AI node, so that I can debug AI behavior
55. As a developer, I want to pull/rebase a worktree during execution, so that parallel work items don't diverge from main
56. As a developer, I want rebase conflicts to route to a Human node, so that I can resolve them manually
57. As a developer, I want the loop to check for pause signals before starting each node, so that I can interrupt execution
58. As a developer, I want running commands to receive cancellation tokens, so that paused execution stops gracefully

### Pull Request Workflow

59. As a developer, I want a pull request to be created after the code is complete, so that reviewers can see the changes
60. As a developer, I want the AI to write the PR title and description, so that PRs are well-documented
61. As a developer, I want commits to be made by AI nodes via MCP tools, so that commit messages are meaningful
62. As a developer, I want to merge pull requests manually through Forgejo, so that I have final approval control
63. As a developer, I want PR comments from Forgejo to appear in the event log, so that feedback is captured in the loop context
64. As a developer, I want webhook-based comment sync from Forgejo, so that comments appear in real time
65. As a developer, I want the branch to be deleted after merge, so that the repository stays clean
66. As a developer, I want the work item to transition to Merged state automatically when the PR is merged, so that the board reflects reality

### Crash Recovery

67. As a developer, I want loop runs to recover after a container restart, so that work is not lost
68. As a developer, I want cmd nodes to auto-retry on recovery, so that idempotent commands continue
69. As a developer, I want AI nodes to auto-retry on recovery, so that lost LLM calls are retried
70. As a developer, I want partial input in Human nodes to be preserved across restarts, so that I don't lose my review comments
71. As a developer, I want worktree health to be validated on recovery, so that corrupted worktrees are detected
72. As a developer, I want to configure recovery policy per loop template (auto-resume, needs review, cancel), so that I control risk per workflow

### Retention & Cleanup

73. As a developer, I want to configure event log retention period, so that storage doesn't grow unbounded
74. As a developer, I want failed runs to retain their event logs until manually closed, so that I can investigate failures
75. As a developer, I want successful runs' event logs to be cleaned up after the retention period, so that storage is managed

### Observability

76. As a developer, I want structured JSON logs, so that I can parse and filter logs in the container environment
77. As a developer, I want to configure log level at runtime through the UI, so that I can debug without restarting
78. As a developer, I want git command output logged, so that I can debug worktree issues
79. As a developer, I want LLM API call details logged (latency, token usage), so that I can track costs
80. As a developer, I want MCP tool invocations logged, so that I can audit AI actions
81. As a developer, I want a health endpoint that checks database, disk space, and remote provider connectivity, so that I can monitor system health
82. As a developer, I want an optional Prometheus metrics endpoint, so that I can integrate with monitoring systems

### Authentication

83. As a developer, I want to log in with username and password, so that access to ILD is controlled
84. As a developer, I want API keys to be stored securely, so that credentials are not exposed
85. As a developer, I want sessions to persist across page reloads, so that I don't need to re-authenticate

## Implementation Decisions

### Module Architecture

The system is organized into the following deep modules:

1. **Loop Engine** — Core orchestration: executes loop runs, schedules nodes, counts edge traversals, detects cycles, manages pause/resume, handles node retry logic
2. **Event Log** — Append-only event store with monotonic sequence numbers, snapshot derivation for current state, retention policy enforcement
3. **Remote Provider** — Pluggable interface (`IRemoteProvider`) for git providers (Forgejo first, extensible to GitHub/GitLab). Handles PR creation, webhook registration, comment polling
4. **Repository Manager** — Worktree lifecycle (create/destroy/validate), branch management, git operations (pull, rebase, commit, push)
5. **AI Provider** — LLM provider abstraction (`ILlmProvider`), prompt template rendering with placeholder substitution, MCP tool orchestration with per-node tool allowlists
6. **Workitem Manager** — Workitem lifecycle, dependency graph with cycle detection, state machine transitions, readiness evaluation
7. **Loop Template Manager** — Template CRUD, auto-versioning on save, validation (reachability, required nodes), cloning
8. **PR Sync Service** — Webhook ingestion from git providers, comment-to-event translation, merge state detection
9. **Auth Service** — Username/password authentication, session management, API key storage
10. **Recovery Manager** — Crash recovery orchestration, worktree health validation, state reconstruction from event log

### Stack

- **Backend**: .NET with EF Core + SQLite, auto-migrate on startup
- **Frontend**: Vite + SPA
- **Real-time**: SignalR (WebSockets) with hub-based channels and event replay on reconnect
- **Auth**: Simple username/password, stored in config
- **Logging**: Serilog with structured JSON output, runtime log level control

### Database Schema

Core tables: `RemoteProvider`, `Repository`, `LoopTemplate`, `LoopTemplateVersion`, `LoopNode`, `LoopNodeEdge`, `WorkItem`, `WorkItemDependency`, `LoopRun`, `LoopRunNode`, `LoopRunEdgeTraversal`, `EventLog`, `AiProvider`, `User`

Loop templates are normalized relational model. Node config is JSON per node type. Templates auto-version on save; workitems pin version at run start.

### Loop Execution Model

- General directed graph (cycles allowed), not a DAG
- Per-edge max traversals configured on source node, plus global safety net
- Event-sourced with snapshot: `EventLog` is append-only with monotonic sequence; `LoopRunState` snapshot updated on each event
- Node states: `Pending` → `Running` → `Succeeded`/`Failed`/`Skipped`/`WaitingHuman`
- Per-node retry count; human retry resets cycle counter
- Cooperative pause: engine checks `IsPaused` flag before each node; running commands receive cancellation tokens

### AI Node Model

- Prompt templates with placeholders: `{{WorkItem.Title}}`, `{{WorkItem.Description}}`, `{{EventLog.LastN}}`, `{{EventLog.Summary}}`, `{{Node.Input}}`, `{{WorkTree.Diff}}`, `{{WorkTree.File:path}}`, `{{PreviousNode.Output}}`
- Multi-provider via `ILlmProvider` (OpenAI-compatible API first), per-node provider selection with global default
- MCP tools: git (scoped to `ild/*` branches), file read/write (worktree-scoped), command execution (allowlist + timeout)
- Tool allowlist configurable per node; default is read-only

### PR Workflow

- PR created after code is complete, AI writes title and description
- Commits made by AI nodes via MCP tools
- Human merge only (v1)
- Forgejo webhooks push PR comments to ILD event log
- Squash merge on merge, branch deleted post-merge

### Deployment

- Single container image, worktrees on host-mounted volume
- Built-in Kestrel web server
- Seed loop templates on first run
- Configurable event log retention; failed runs retained until manually closed

### API

- RESTful API, versioned from day one (`/api/v1/...`)
- Cursor-based pagination for event logs
- Large payloads (>10KB) stored on disk, database stores path reference
- SignalR hubs: `LoopRunHub` (node state + events), `WorkItemHub` (state changes)

## Testing Decisions

### What Makes a Good Test

Tests should verify external behavior and observable outcomes, not internal implementation details. Each test should:
- Exercise a module through its public interface
- Use mocks/fakes for external dependencies (database, filesystem, LLM providers, git providers)
- Verify state transitions, event sequences, and business rules
- Be deterministic and fast (no real network calls, no real git operations)

### Modules With Tests

1. **Loop Engine** — Test node execution sequencing, edge traversal counting, cycle detection, pause/resume behavior, retry logic, recovery policy enforcement. Mock the event log, node executors, and worktree manager.
2. **Event Log** — Test append-only semantics, sequence numbering, snapshot derivation, retention policy enforcement (deletion after retention period, failed run preservation). Mock the storage layer.
3. **Workitem Manager** — Test dependency graph validation (cycle detection), state machine transitions, readiness evaluation (all deps merged), workitem lifecycle. Mock the database and loop engine.
4. **Loop Template Manager** — Test auto-versioning, validation rules (start/cleanup required, reachability), cloning, version pinning semantics. Mock the database.
5. **Auth Service** — Test login/logout, session lifecycle, password hashing, API key storage. Mock the user store.
6. **Remote Provider** — Test PR creation payload, webhook registration, comment parsing. Mock HTTP calls to the git provider API.
7. **AI Provider** — Test prompt template rendering with all placeholders, provider selection (per-node vs global default), MCP tool allowlist enforcement. Mock the LLM provider and MCP tools.

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
- The visual loop editor is a key UX surface — investing in a good node graph editor library is important
- MCP tool access should be conservative by default — read-only unless explicitly granted
