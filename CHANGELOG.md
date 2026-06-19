# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- The taskboard no longer gets stuck showing a stale status (e.g. a newly created work item parking for human input still showing "Running") when a burst of work-item-hub events arrives in quick succession. Each event triggered a `getById`, and an earlier fetch the server answered with an older status could resolve _after_ a later one and clobber the fresher state, with nothing to correct it. Each fetch now carries a per-item request ordinal and its result is applied only while it is still the latest request for that item, so a late, stale response is dropped instead of reverting the card.

### Added

- The run timeline/event log now records an `EdgeTraversed` entry naming the edge the engine took out of each node — a custom edge's name (e.g. `Respond`, `true`/`false`), or the role (`OnSuccess`/`OnFailure`) for default and fallback edges — so routings are visible as the edge taken rather than only the node's Succeeded/Failed status. Best-effort and attributed to the node the edge left; routing behavior is unchanged.
- The run-details timeline now labels each edge by the actual edge the engine traversed (resolved from the node's persisted `IncomingEdgeId`, now exposed on the run-node API), so a custom routing shows its name (e.g. `Respond`) instead of the generic `custom`. Runs predating edge persistence fall back to the previous inferred-from-status label.


## [0.2.0] - 2026-06-15

### Fixed

- A run's worktree and per-run branch now live exactly as long as its run row: only run deletion — manual (`DELETE /api/v1/loopruns/{id}`, work-item delete) or the retention sweeper — destroys them, through a shared `IRunReclaimer` with a fallback that locates the base repo through the run's Repository when the worktree is already gone. Sending a work item to Done/Backlog finishes the run but keeps its worktree and branch inspectable. The run row is only deleted once reclamation is verified (manual delete returns 409 on failure), so a failed worktree/branch delete is retried by a later sweep instead of leaking untracked disk. Spilled event-log payload files are deleted together with the run.
- The engine now reloads the run row before persisting each node outcome, so pausing, cancelling, or pinning (`Retain`) a run during a long node execution is no longer silently reverted by the node's completion write — previously a pinned run could still be deleted by retention and a cancel could be undone.
- Startup reconciliation now includes runs parked at Human/PR nodes (`WaitingHuman`) and re-tracks their work items so server heartbeats resume — previously the stale-item reclaimer would hand the item to a second concurrent run ~15 minutes after a human resumed it. Runs whose work item the server has since finished, reclaimed, or deleted are cancelled instead of being blindly resumed into a duplicate, and recovery honors the template's `RecoveryPolicy` (which is now actually pinned onto each run at start).
- A work item claim whose run fails to start (e.g. broken template) is handed back to HumanFeedback instead of remaining stuck in Running while heartbeated forever, permanently occupying a scheduler slot.
- A PR-merge webhook now books the merge on the run that owns the PR (matched by URL). Merging an old run's still-open PR no longer marks the current run merged or flips the work item to Done while another run is mid-flight, and a merge no longer marks the work item Done before the resumed run has executed its post-merge nodes.
- The worktree retention sweeper re-checks each run's status and `Retain` pin just before reclaiming (a run retried back to Running mid-sweep is left alone), and its first pass now happens 10 minutes after startup instead of 6 hours, so frequently-restarted deployments still reclaim disk.
- A run can no longer get permanently stuck in Running with no task driving it. The engine's launch path is now single-owner — one atomic ownership claim per run, so a launch/relaunch race (e.g. the periodic scheduler's resume colliding with a live loop) can't start a second loop or leave a run un-driven — and `ResumeRecoveredRunAsync` no-ops on an already-driven run instead of clobbering its in-flight node. A new `StuckRunWatchdog` background service is the backstop: every minute it recovers runs that are Running with no live driver (honoring the run's `RecoveryPolicy`), covering crash modes the engine's own exception handler can't see. It keys on absence of a driver, never elapsed time, so a legitimately long-running AI node is never interrupted; runs intentionally parked by the capacity gate (work item `WaitingForIld`) or awaiting a human are left for the scheduler/human to resume.
- The Start node now fails fast when the base-repo fetch fails instead of starting from stale state.
- Work item `Started`/`Completed` timestamps are populated from the latest run.
- The file explorer and Changes view live-refresh; folders collapse by default in the explorer and expand by default in the Changes view.
- Numeric work item status from SignalR events is normalized to the correct `RemoteWorkItemStatus` ordering, fixing mismatched board placement.
- The live view no longer clips the last line of output, and the new work item dialog clears its fields when reopened.

### Security

- Provider API keys and webhook secrets (`RemoteProvider.ApiKey`/`WebhookSecret`, `AiProvider.ApiKey`) are now encrypted at rest with AES-256-GCM when the new `ILD_SECRET_KEY` environment variable is set. Behaviour is backwards-compatible: when the key is unset, secrets are stored as plaintext and a startup warning is logged; legacy plaintext rows remain readable after a key is added. See [docs/configuration.md](docs/configuration.md#secret-encryption-at-rest).
- Webhook secrets are now masked (`***`) in `GET /api/v1/remote-providers` responses, matching the existing treatment of API keys, and a `hasWebhookSecret` flag indicates whether one is set.
- Added [SECURITY.md](SECURITY.md) documenting the trust model and vulnerability reporting process.

### Changed

- AI nodes now use a single `prompt` field regardless of whether they run in a fresh or resumed session; prompt variation across turns is now modeled explicitly with `Prompt` nodes in the graph.
- AI adapters now always receive one prompt from the AI node; session handling only controls binding and resume behavior.
- Opencode managed session import/export now only runs for AI nodes that explicitly enable session usage.
- The seeded templates now insert `Prompt` nodes ahead of session-backed AI nodes wherever the first turn should differ from later follow-up turns.
- Each run now gets its own git branch (`ild/wi-<workItemId>-run-<runId>`) and worktree instead of a shared per-work-item branch — this fixes prior-run commits leaking into a new run via rebase. The Cleanup node no longer destroys the worktree; finished runs keep their worktree so they stay inspectable, and re-running a work item now opens a separate PR per run.
- Loop tags are visually distinguished (purple background) from other tags in the taskboard filter chips.
- The engine enforces at most one active run per work item, closing a race that could start two concurrent runs.

### Added

- A `WorktreeRetentionSweeper` reclaims a finished run's worktree, branch, and record once it has been terminal longer than the configurable `run.retentionDays` setting (default 30; `0` disables; the run a still-active work item points at is preserved). Individual runs can be pinned with **Retain** (`PUT /api/v1/loopruns/{id}/retain`) so retention never deletes them. See [ADR-0008](docs/adr/0008-worktree-and-branch-per-run.md).
- Added a `Prompt` loop node type that renders a prompt template with the same placeholder-aware editor used by AI and PR prompt fields, then emits the rendered text as node output.
- Added a **Push branch** button to the work item overview (`POST /api/v1/workitems/{id}/push-branch`) that commits all uncommitted changes in the current run's worktree and pushes its branch to origin, using the same built-in repository functionality as the PR node — for keeping work produced by a loop that has no PR node.
- The work item overview now shows a **Loop** row while an item is mid run, naming the run's pinned loop template and the node the engine is currently on (e.g. `build-loop · Implement`).
- The work item dialog is now a full-screen, tabbed view (Overview, Conversation, Runs, Files, Terminal) replacing the previous in-dialog switcher. Edit moved to the footer and Delete now lives inside the edit view.
- **Run analytics & AI cost dashboard** — durable usage rollup with per-provider and time-range filters surfacing run counts, throughput, and token/cost trends.
- **Halt & steer** — interrupt a running AI node from the live view, inject guidance, and resume; the Halt/steer control lives on the Overview rather than buried in a node.
- The live view streams complete run output with replay and an xterm-based renderer; interactive terminals support copy/paste (Ctrl/Cmd+C/V), including in insecure-context previews.
- A **Files** tab with a live file explorer and a **Changes** diff view in the work item dialog.
- **Retry from node** on the work item Runs tab, re-entering a run at a chosen node.
- A **Merge** action (with optional delete-branch) on the PR-feedback panel.
- Named **custom edges** generalize node edge routing; AI nodes now route solely via match rules, with editor support for connected custom edges on AI/Human/PR nodes.
- Taskboard search plus repository and tag filters; running cards show the current step and elapsed time, and the Backlog and Done columns paginate. Loop creation and work-item creation now broadcast so the board updates live.
- Work items are deep-linkable via URL, always opening the detail dialog on the Overview tab.
- Opt-in `ild.config` tool install on the Start node, exposed to agents during preview.
- Example loop templates are seeded on a fresh install.
- The PR node renders its prompt template onto the work item, like the Human node.

### Removed

- Backward compatibility for legacy AI node prompt fields and editor shims for loading or saving the old prompt model.
- The **Mark Merged** button on the work item dialog; merge state is now driven by PR-merge webhooks and the explicit Merge action.
- The obsolete AI `RejectPattern` config; AI routing is now expressed solely through named match-rule custom edges (existing `rejectPattern` config is migrated automatically).

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
