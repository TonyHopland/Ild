# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- AI nodes now use a single `prompt` field regardless of whether they run in a fresh or resumed session; prompt variation across turns is now modeled explicitly with `Prompt` nodes in the graph.
- AI adapters now always receive one prompt from the AI node; session handling only controls binding and resume behavior.
- Opencode managed session import/export now only runs for AI nodes that explicitly enable session usage.
- The seeded templates now insert `Prompt` nodes ahead of session-backed AI nodes wherever the first turn should differ from later follow-up turns.

### Added

- Added a `Prompt` loop node type that renders a prompt template with the same placeholder-aware editor used by AI and PR prompt fields, then emits the rendered text as node output.

### Removed

- Backward compatibility for legacy AI node prompt fields and editor shims for loading or saving the old prompt model.

## [0.1.0] - 2026-05-12

### Added

- **WorkItem Server** — standalone REST service as authoritative source for work items with atomic claim, heartbeat, and stale reclamation
- **Taskboard UI** — React frontend with columns for Backlog, Work Queue, Ready, Running, Human Feedback, Waiting For Ild, and Done
- **Loop Engine** — graph-based workflow executor with retries, OnFailure routing, MaxTraversals cycle protection, pause/resume, and crash recovery
- **Node executors** — Start, Cmd, AI, Human, PR, and Cleanup node types for configurable workflows
- **Loop Template Editor** — visual graph editor with versioning; every save creates an immutable LoopTemplateVersion
- **Remote Provider system** — configurable git provider connections with WorkItem server URL, poll schedule, grace period, and max concurrency
- **AI Provider management** — adapter-driven AI execution resolved by provider type, with per-provider usage statistics (token/cost tracking)
- **Repository management** — configured repositories for work item execution
- **SignalR real-time updates** — live node state changes, event log entries, and run state transitions pushed to the frontend
- **MCP Server** — Model Context Protocol server for AI agent integration with work item operations
- **Containerized deployment** — Docker Compose setup with ILD API + frontend and WorkItem Server, configurable toolchain (opencode, Node, .NET SDK, Chrome)
- **Tag-based loop template resolution** — work item tags determine which loop template executes, with validation and escalation to HumanFeedback
- **Human feedback workflow** — configurable grace period polling, fast cadence during active feedback, and Approve/Reject UI
- **Startup reconciliation** — on restart, queries remote server for each tracked work item's status and resumes, tracks, or cleans up accordingly
- **Recovery policies** — AutoResume, NeedsReview, or Cancel for in-flight runs after a crash
- **Authentication** — session-token auth with PBKDF2-SHA256 password hashing, bootstrapped via environment variables
- **Webhook integration** — Forgejo/Gitea PR webhook ingress for merge sync
- **AI usage statistics** — token and cost tracking per AI provider

### Fixed

- SignalR live updates not reflecting node state changes in real time
- Work item delete failing with FK constraint due to missing dependent checks
- Container build issues with work item UI view
- MCP server proxy fixes for work item operations
- Ellipsing of input/output textboxes in live loop run view
- Session handling and live view stability during running loops

### Changed

- WorkItemManager is now fully remote-backed; all writes hit the WorkItem Server first, then mirror to local sidecar
- Hard-cut all reads through WorkItem Server; local DB is sidecar only
- Pruned dead code per WorkItemServer-PRD alignment
- Tag-driven loop template resolution with per-tag autocomplete and repositoryId validation
