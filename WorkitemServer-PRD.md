# WorkItem Server — Product Requirements Document

## 1. Overview

Extract the WorkItem Manager from ILD into a standalone, independent REST server. Multiple ILD instances connect to this shared server to coordinate work item lifecycle, claim work, and resolve human feedback — while each ILD instance retains full ownership of loop execution, loop templates, git operations, and local state.

### 1.1 Goals

- Multiple developers each run their own ILD container, all connecting to a shared WorkItem server
- The WorkItem server is swappable — future integrations (GitHub Issues, Jira, etc.) replace the server without changing ILD internals
- ILD polls for ready work autonomously — no manual "start" button required
- Agent session continuity: work items stay on the ILD instance that claimed them until completion or cleanup

### 1.2 Non-Goals

- Dual-mode support (local + remote) — hard break, remote only
- Remote server awareness of loop templates, loop runs, or execution details
- Ownership tracking on the server side

---

## 2. Remote Server Architecture

### 2.1 Technology

- Standalone service with its own database
- HTTP REST API
- API key authentication (one key per ILD instance)

### 2.0 Repository Awareness

The server does not store repository entities. Repository URL and API key are configured per remote provider in ILD's settings. ILD infers the repository for every work item from this 1:1 provider-to-repository mapping.

### 2.2 Data Model

The server only knows about work items. No loop templates, no loop runs, no ownership tracking.

**WorkItem entity:**

| Field          | Type      | Description                                                                            |
| -------------- | --------- | -------------------------------------------------------------------------------------- |
| `Id`           | Guid      | Unique identifier                                                                      |
| `Title`        | string    | Work item title                                                                        |
| `Description`  | string    | Work item description                                                                  |
| `CreatedBy`    | string    | Logged-in user or Agent-RunId                                                          |
| `CreatedDate`  | DateTime  | Creation timestamp                                                                     |
| `Priority`     | enum      | Work item priority                                                                     |
| `Tags`         | string[]  | Tags used for loop template matching and user-defined categorization                   |
| `Status`       | enum      | Current state (Backlog, WorkQueue, Ready, Running, HumanFeedback, WaitingForIld, Done) |
| `Dependencies` | Guid[]    | IDs of work items this item depends on                                                 |
| `Conversation` | Message[] | History of AI↔Human dialogue entries                                                   |

**Conversation Message:**

| Field       | Type     | Description                |
| ----------- | -------- | -------------------------- |
| `role`      | string   | `"ai"` or `"human"`        |
| `content`   | string   | Message text               |
| `timestamp` | DateTime | When the entry was created |

### 2.3 State Machine

There is no fixed traversal order. Any state can transition to any other state. The server:

- Accepts all transitions — never rejects
- Auto-advances through intermediate states if needed
- Appends a Conversation entry when transitioning to response states (HumanFeedback, WaitingForIld, Done)
- Validates dependency readiness on `Transition(Running)` — returns failure if dependencies are unsatisfied
- Enforces atomic claim on `Transition(Running)` — first ILD to claim wins, subsequent claims return failure with actual status

### 2.4 REST API

| Endpoint                               | Method | Purpose                                            |
| -------------------------------------- | ------ | -------------------------------------------------- |
| `/workitems`                           | POST   | Create work item                                   |
| `/workitems`                           | GET    | List work items (filter by status, tags)           |
| `/workitems/{id}`                      | GET    | Get single work item                               |
| `/workitems/{id}`                      | PUT    | Update title/description                           |
| `/workitems/{id}/transition`           | POST   | Transition status (claim, resume, cleanup, etc.)   |
| `/workitems/{id}/dependencies`         | GET    | List dependencies                                  |
| `/workitems/{id}/dependencies`         | POST   | Add dependency                                     |
| `/workitems/{id}/dependencies/{depId}` | DELETE | Remove dependency                                  |
| `/workitems/{id}/feedback`             | POST   | Submit human feedback                              |
| `/workitems/{id}`                      | DELETE | Delete work item                                   |
| `/workitems/poll`                      | GET    | Poll for Ready + active items (includes heartbeat) |

**Transition request:**

```json
{
  "targetStatus": "Running",
  "reason": "string | null",
  "actions": "string | null"
}
```

**Transition response (success):**

```json
{
  "success": true,
  "actualStatus": "Running"
}
```

**Transition response (failure — Running claim denied):**

```json
{
  "success": false,
  "actualStatus": "Running",
  "reason": "Already claimed"
}
```

**Transition behavior:**

- `Transition(Running)`: validated — server checks dependencies are satisfied and item isn't already Running. Returns failure if either check fails.
- All other transitions: always succeed. Server auto-advances through intermediate states, never rejects. Returns the actual resulting status.
- `reason` is appended to the Conversation array when transitioning to response states (HumanFeedback, WaitingForIld, Done).
- `actions` is stored on the work item for HumanFeedback transitions (available to frontend for rendering action buttons).

**Work item creation:**

- New work items default to `Backlog` status unless `forceStatus` is provided in the create request.
- `repositoryId` is not stored on the server — ILD infers the repository from the remote provider configuration (1:1 mapping).

**Poll request:** `GET /workitems/poll?activeIds=id1,id2,...`
Returns status updates for active items + list of Ready items. Serves as heartbeat — server detects stale items that haven't been seen within the configured timeout (default: 15 minutes). Stale items are auto-transitioned back to Ready.

**Poll response:**

```json
{
  "activeItems": [
    { "id": "guid", "status": "WaitingForIld", "tags": ["bug-fix"], "conversation": [...] }
  ],
  "readyItems": [
    { "id": "guid", "title": "...", "description": "...", "tags": ["feature"], "priority": "High" }
  ]
}
```

The `activeItems` array returns full work item data for each ID that was sent in `activeIds`. The `readyItems` array returns work items eligible for claiming.

---

## 3. ILD Instance Architecture

### 3.1 Configuration (Settings Page)

A "Remote Provider" section on the settings page with:

| Field                     | Type     | Description                                                         |
| ------------------------- | -------- | ------------------------------------------------------------------- |
| Server URL                | text     | Remote WorkItem server address                                      |
| WorkItem API Key          | password | Auth key for workitem server                                        |
| Repository URL            | text     | Git repository URL (1:1 mapping)                                    |
| Repository API Key        | password | Auth key for git operations                                         |
| Polling Schedule          | text     | Cron expression for new work polling                                |
| Grace Period              | number   | Minutes to wait for human feedback before resuming new work polling |
| Max Concurrent Work Items | number   | Maximum parallel work items (default: 1)                            |

### 3.2 Polling Loop

A single background poller with adaptive frequency:

- **During grace period** (at least one active work item is in HumanFeedback): polls every 5 seconds
- **After grace period expires**: reverts to configured cron interval
- **Below max concurrency during grace**: still picks up new Ready work items alongside checking for WaitingForIld

Each poll:

1. Sends active work item IDs as heartbeat
2. Checks if any active items transitioned to `WaitingForIld`
3. Checks for new Ready work items (if below max concurrency)
4. On success: transitions to Running (claim), creates local LoopRun, kicks off LoopEngine
5. On failure: skips the item

### 3.3 Local State

ILD's local SQLite stores:

- **Active work items**: mapping of work item IDs to current status (persisted across restarts)
- **LoopRuns**: full execution history (user-deletable)
- **Loop templates**: local execution configuration
- **Repositories**: local git worktree state

### 3.4 Startup Reconciliation

On startup, ILD:

1. Reads local active list from SQLite
2. Queries server for current status of each active work item
3. If server says Running and ILD has it locally: resume execution
4. If server says Done: clean up locally
5. If server says unexpected state: handle gracefully (clean up)

### 3.5 Work Item Claim Flow

```
Poll → find Ready item → POST transition(Running) → server validates (deps, atomicity)
  → success: create local LoopRun, start LoopEngine
  → failure: skip, next poll cycle
```

### 3.6 Human Feedback Flow

```
LoopEngine → HumanFeedback node → ILD transitions work item to HumanFeedback on server
  → user responds via frontend → ILD proxies to server's feedback endpoint
  → server transitions to WaitingForIld, appends conversation entry
  → ILD's poller detects WaitingForIld → transitions to Running → resumes execution
  → if grace period expires: ILD resumes normal polling, user can still resolve feedback
```

### 3.7 Tag-to-Loop Template Matching

- Tag name must match a loop template name on the ILD instance
- **No match**: ILD transitions work item to HumanFeedback with message "No loop found for existing tags"
- **Multiple matches**: ILD transitions to HumanFeedback with message to disambiguate
- Tags are set by the creator (user or agent), not inherited from parent

### 3.8 AI Agent Work Item Creation

- MCP server stays in ILD, proxied to remote server
- Agent calls `create_workitem` → ILD proxies to remote server
- Agent specifies tags freely
- `CreatedBy` is set to the agent's LoopRun ID

### 3.9 Crash Handling

- ILD self-manages active work items
- Server detects stale work items via heartbeat timeout (piggybacked on poll)
- On crash: ILD's local active list is reconciled on next startup
- Server auto-transitions orphaned items back to Ready after heartbeat timeout

---

## 4. Frontend Changes

### 4.1 Conversation View

Replaces the current single-string `HumanFeedbackReason` display:

- Shows full AI↔Human dialogue history
- Messages are timestamped and role-labeled
- Appended by server on response state transitions

### 4.2 Tag Management

- Tags are editable on work item creation
- Tags are editable on work item update
- Tags determine which loop template will execute the work item

### 4.3 LoopRun History Cleanup

- User can delete old LoopRuns from the local ILD instance
- Accessed via the existing LoopRun UI

### 4.4 All Requests Proxied Through Local ILD

Frontend communicates with local ILD API. Local ILD proxies work item operations to the remote server. Frontend stays on local SignalR for real-time notifications.

---

## 5. Refactoring Steps

### Phase 1: Unify WorkItem Access

1. Add generic `TransitionAsync(Guid workItemId, WorkItemStatus status, string? reason, string? actions, Guid? currentLoopRunId)` to `IWorkItemManager`
2. Refactor LoopEngine to use `IWorkItemManager` instead of direct `IWorkItemStore` calls
3. Existing per-status methods (`TransitionToRunningAsync`, etc.) become thin wrappers around the generic method
4. Verify all work item access flows through the single interface

### Phase 2: Build Remote Server

1. Implement simplified WorkItem data model (Id, Title, Description, CreatedBy, CreatedDate, Priority, Tags, Dependencies, Conversation, Status)
2. Implement REST API endpoints
3. Implement transition validation (dependency check, atomic claim, auto-advance)
4. Implement heartbeat/stale detection via poll endpoint
5. Implement API key authentication

### Phase 3: Build ILD Remote Client

1. Create `IWorkItemManager` implementation that calls remote REST API
2. Build polling loop with adaptive frequency
3. Add local active work item tracking in SQLite
4. Add startup reconciliation
5. Implement settings page for remote provider configuration
6. Proxy MCP tools through local ILD to remote server

### Phase 4: Frontend Updates

1. Build conversation view replacing feedback reason display
2. Add tag management to work item creation/edit forms
3. Add LoopRun deletion capability
4. Wire up settings page for remote provider configuration

### Phase 5: Hard Switch

1. Remove local WorkItemManager implementation
2. Remove local work item storage from ILD's SQLite
3. All work item operations go through remote server

---

## 6. Resolved Decisions

- **Heartbeat timeout**: 15 minutes default, configurable on server
- **WaitingForIld**: first-class status in the state machine, not a transition hint
- **Conversation append**: only on transitions to HumanFeedback, WaitingForIld, or Done

## 7. Open Decisions

- Conversation entry size limits / pagination strategy
- Rate limiting on remote server API
