# Issue #4 — Backend: Effective Input Enrichment for NodeStarted

## What to build

Enrich the `NodeStarted` event with structured JSON containing the effective input that the node actually executed with.

Currently `NodeStarted` logs only `"{node.Label} started"`. The new payload includes the resolved command, prompt, and context so the frontend can display what the node was given to work with.

### Payload shape per node type

- **Cmd**: `{"nodeType": "Cmd", "command": "<resolved shell command>"}`
- **AI**: `{"nodeType": "AI", "prompt": "<initialPrompt or loopPrompt>", "context": {"workItemTitle": "...", "workItemDescription": "...", "priorEventLog": [...]}}`
- **Human**: `{"nodeType": "Human", "prompt": "<feedback request>"}`
- **Start**: `{"nodeType": "Start", "message": "initialized"}`
- **Cleanup**: `{"nodeType": "Cleanup", "message": "cleanup"}`

The structured JSON is stored in the `output` parameter of `LogEventAsync`, which concatenates it with the message into the event log `Data` field. The same data is broadcast via `EventLogged` SignalR (wired in issue #1).

### Implementation

In `LoopEngine.ExecuteNodeWithRetryAsync`, before calling `LogEventAsync` for `NodeStarted`, build the structured payload from the node's config and the work item context. For AI nodes, include the work item title, description, and prior event log summary (the same data already assembled for `AgentExecutionContext`).

## Acceptance criteria

- [ ] `NodeStarted` event payload contains structured JSON with effective input
- [ ] Cmd nodes include the resolved command string
- [ ] AI nodes include prompt + context (work item title, description, prior event log)
- [ ] Human nodes include the feedback request
- [ ] Start/Cleanup nodes include a status message
- [ ] Payload is stored in the event log and broadcast via SignalR
- [ ] Existing behavior is preserved (node label + "started" still in message)

## Blocked by

- Blocked by #1 (requires `LogEventAsync` → SignalR wiring)
