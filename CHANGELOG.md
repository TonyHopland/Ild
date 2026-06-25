# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.0] - 2026-06-25

### Added

- **AI Providers page** — a redesigned page that lists each managed coding agent (Pi, OpenCode, Claude Code) with its installed and latest version and a single action button: **Install** when nothing is installed, **Update {old} → {new}** when behind, **Up to date** when current, and **Unavailable** when not installed and the npm registry can't be reached.
- **Managed coding agents** — Pi, OpenCode, and Claude Code are installed onto the persistent `/data` volume on demand (npm install into a versioned dir followed by an atomic `current` pointer-file swap) and resolved from there at launch; an explicit config `binaryPath` still wins, and the baked-in/PATH copy remains the offline fallback. New `GET /api/v1/managedagents` and `POST /api/v1/managedagents/{key}/update` endpoints back the page.
- **Automatic agent provisioning** — a `ManagedAgentProvisioner` hosted service installs every agent an existing provider uses at startup, and on demand when a provider for a not-yet-installed agent is added or changed, so a fresh or freshly upgraded deployment no longer fails its first AI run on a missing CLI. Installs run detached and best-effort, never blocking host startup or the HTTP request.
- Chat now shows an in-progress indicator for the whole turn — "Thinking" before any tokens, "Responding" while text streams — with an animated pulsing dot that respects `prefers-reduced-motion`, so an in-flight reply is no longer indistinguishable from a finished one.

### Changed

- **Coding agents are no longer baked into the container image** — the `WITH_OPENCODE` / `WITH_PI` / `WITH_CLAUDE_CODE` build args and their Dockerfile install blocks are removed, and dropped from `docker-compose.yml`, `.env.example`, the publish workflow, and the configuration docs. A fresh deployment installs each agent once from the AI Provider page, and installs persist on `/data` across redeploys. `WITH_NODE` stays — Node/npm is what the runtime installs and version checks use.
- An agent update keeps the previous install alongside the new one (pruning bounded to two installs, current + previous) so an in-flight run that lazily `require()`s files from its version dir isn't pulled out from under it mid-update.
- **Event-log payloads are stored inline in Postgres** instead of being offloaded to a file when over 10 KB. Postgres already TOASTs and compresses large text values out-of-line, so the offload bought nothing while creating split state, a second read path, and a data-loss bug (every redeploy wiped the payload files but kept the rows, so the payload fetch 404'd forever). A startup `EventLogPayloadInliningMigrator` slurps any previously offloaded files back into the row and clears dangling paths, and the now-dead `/events/payload` endpoint, `hasPayload` flag, and payload-file retention/delete paths are removed.
- **Image publishing is release-only** — CI now pushes the GHCR images (`ghcr.io/tonyhopland/ild` and `ghcr.io/tonyhopland/ild-workitem-server`) only on a `vX.Y.Z` release tag, not on every merge to `main`. The moving `main` image tag is dropped. See [ADR-0012](docs/adr/0012-ghcr-image-tagging-strategy.md).

### Fixed

- Stranded WorkQueue items are now reconciled by a new `WorkQueueReconciler` background service: an item that enters the queue with its dependencies already Done, or one left behind by a crash or a lost promotion under concurrent completion, is promoted to Ready instead of waiting forever for a Done transition that will never fire. Deleting a work item also scrubs its id from every other item's dependency list, so dependents are no longer blocked permanently by a dangling dependency.
- A parked human-feedback node resumed via `SignalNodeResultAsync` — from the automated PR/CI poller, webhook PR sync, or an actual human response — now moves the work item out of the HumanFeedback column to Running, mirroring the other resume paths, so the board and the engine no longer diverge while the run executes.
- A human-feedback node no longer renders a stale `{{Var.*}}` value: `GetVariablesAsync` now reads with `AsNoTracking()` so it returns fresh column values after `set_loop_variable` updates a variable from a separate scope, instead of an already-tracked stale instance via EF identity resolution.
- The AI Provider "Open terminal" action now resolves the CLI from the agent's `/data` install (or PATH) the same way the loop adapters do, instead of a bare command name that isn't present on a fresh machine, and sends an actionable "install it from the AI Provider page" message when nothing resolves rather than an opaque PTY spawn error.

## [0.3.0] - 2026-06-23

### Added

- **Context-aware AI chat** — a per-user, draggable and resizable in-app chat bubble that talks to ILD's work-item tools. It can create, edit, and delete work items (editing or deleting only the items its own session created), and is aware of the work item, worktree, and live loop-editor context it is opened from. When a Loop Editor is open it can read the in-flight loop document (`get_current_loop`) and direct-apply a full-document edit to the open canvas (`update_current_loop`) without saving — persistence stays human-only. New chats pre-select the default AI provider. See [ADR-0011](docs/adr/0011-context-aware-chat.md).
- **Condition node** — a new graph node type that evaluates a single predicate against run/work-item state (text match, PR exists, has tag) and routes to a fixed `true`/`false` edge without invoking AI, running a command, or touching the worktree. Includes engine executor, template validation, and loop-editor support.
- **Per-run loop variables** — AI nodes can read and write named variables scoped to a run; the run view renders them as markdown. New `{{Conversation.*}}` placeholders expose run history to prompt templates.
- **Preview services** — start, stop, and configure preview services individually, with a table view showing per-service state, an editable port, a link, and live logs.
- **PR auto-merge and full PR view** — PRs tagged `AutoMerge` are merged automatically once approved and green; the heartbeat poller fires PR-state custom edges and surfaces a full PR view with CI verdict and reviews.
- **Abandon run** — stop a running run, including while it is parked for human feedback, from the work item Runs/Action tab.
- Fork an AI session into a new named session so a later node can reuse it.
- Taskboard cards parked awaiting merge show PR status tags, and the run timeline/event log now records an `EdgeTraversed` entry naming the edge the engine took out of each node — a custom edge's name (e.g. `Respond`, `true`/`false`), or the role (`OnSuccess`/`OnFailure`) for default and fallback edges — so routings are visible as the edge taken rather than only the node's Succeeded/Failed status. Best-effort and attributed to the node the edge left; routing behavior is unchanged.
- Node transitions are now marked in the live output stream.
- The viewed loop template now persists in the URL so it can be linked and survives a reload.
- **Container image publishing** — CI builds and pushes both deployable images (`ghcr.io/tonyhopland/ild` and `ghcr.io/tonyhopland/ild-workitem-server`) to GHCR from a build-gated job, and both report their stamped version on `/health`. See [ADR-0012](docs/adr/0012-ghcr-image-tagging-strategy.md).

### Changed

- **Work item dialog redesign** — the full-screen V2 dialog is now used for work-item creation (the old modal is removed); the live view and human-feedback pane moved into an **Action** tab, which opens automatically when an item is awaiting human feedback, and the Halt/steer control now lives there too.
- The AI node dialog was redesigned.
- Work item **Description** is now unlimited length.
- Git commit identity can be overridden via environment variables without changing the host default.
- **Loop-editor edge rendering** — multiple edges between the same pair of connectors are now allowed and fanned onto readable, individually clickable lanes with staggered labels; edge labels are the click target for selection, and edges highlight on hover.
- The run-details timeline now labels each edge by the actual edge the engine traversed (resolved from the node's persisted `IncomingEdgeId`, now exposed on the run-node API), so a custom routing shows its name (e.g. `Respond`) instead of the generic `custom`. Runs predating edge persistence fall back to the previous inferred-from-status label.
- The taskboard heading is replaced with a filter + toggle toolbar.

### Fixed

- The taskboard no longer gets stuck showing a stale status (e.g. a newly created work item parking for human input still showing "Running") when a burst of work-item-hub events arrives in quick succession. Each event triggered a `getById`, and an earlier fetch the server answered with an older status could resolve _after_ a later one and clobber the fresher state, with nothing to correct it. Each fetch now carries a per-item request ordinal and its result is applied only while it is still the latest request for that item, so a late, stale response is dropped instead of reverting the card.
- The WorkItem Server's Ready→Running claim is now atomic, closing a race that could let two runs claim the same work item.
- A waiting work-queue item is promoted to Ready when the dependency it was waiting on completes.
- `on_merged` is now wired to the Cleanup node, and a PR failure no longer loops back endlessly.
- A PR keeps its approval when a later `COMMENTED` review follows, and the CI verdict is serialized as its string name so the PR panel renders it.
- `HumanFeedbackReason` is cleared when a human signal resumes a run, and halt state is cleared when a node is retried so the steer dialog no longer sticks.
- The `ild.config` tool install on the Start node is best-effort when no config is present, instead of failing the node.
- A work item edit now broadcasts so the board refreshes without a manual reload.

### Security

- Patched Dependabot advisories in dev-tree dependencies (`vite`, `ws`, `react-router`).

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
