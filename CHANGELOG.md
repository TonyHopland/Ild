# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-05-12

### Added

- **WorkItem Server** — standalone REST service as authoritative source for work items with atomic claim, heartbeat, and stale reclamation
- **Taskboard UI** — React frontend with columns for Backlog, Work Queue, Ready, Running, Human Feedback, Waiting For Ild, and Done
- **Loop Engine** — graph-based workflow executor with retries, OnFailure routing, MaxTraversals cycle protection, pause/resume, and crash recovery
- **Node executors** — Start, Cmd, AI, Human, PR, and Cleanup node types for configurable workflows
- **Loop Template Editor** — visual graph editor with versioning; every save creates an immutable LoopTemplateVersion
- **Remote Provider system** — configurable git provider connections with WorkItem server URL, poll schedule, grace period, and max concurrency
- **AI Provider management** — OpenAI-compatible chat completions with per-provider usage statistics (token/cost tracking)
- **Repository management** — configured repositories for work item execution
- **SignalR real-time updates** — live node state changes, event log entries, and run state transitions pushed to the frontend
- **MCP Server** — Model Context Protocol server for AI agent integration with work item operations
- **Containerized deployment** — Docker Compose setup with ILD API + frontend and WorkItem Server, configurable toolchain (opencode, Node, .NET SDK, Chrome)
- **Tag-based loop template resolution** — work item tags determine which loop template executes, with validation and escalation to HumanFeedback
- **Human feedback workflow** — configurable grace period polling, fast cadence during active feedback, and Approve/Reject UI
- **Startup reconciliation** — on restart, queries remote server for each tracked work item's status and resumes, tracks, or cleans up accordingly
- **Recovery policies** — AutoResume, NeedsReview, or Cancel for in-flight runs after a crash
- **Authentication** — JWT-based auth with PBKDF2-SHA256 password hashing, bootstrapped via environment variables
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
