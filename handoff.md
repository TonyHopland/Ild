# Handoff: Refactor Node Architecture (Work Item #38 "Fix the spagetti")

## Date: 2026-05-20

## Status: Design complete via grill-me, implementation not started

---

## Problem Statement

The current architecture has too much business logic in `LoopEngine` and not enough in the node executors. The user wants nodes to be completely self-contained and stateless, resolving their own dependencies and checking preconditions before doing work. All relevant run state should live on `LoopRun`.

---

## Current Architecture (Before Refactor)

### Key files

- `ILD.Core/Services/Implementations/LoopEngine.cs` — ~1100 lines, monolithic orchestrator
- `ILD.Core/Services/Interfaces/INodeExecutor.cs` — `Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)`
- `ILD.Core/Services/Implementations/Executors/NodeExecutors.cs` — Start, Cmd, AI, Human, Prompt, Cleanup executors
- `ILD.Core/Services/Implementations/Executors/PRNodeExecutor.cs` — PR node executor
- `ILD.Core/Services/Implementations/Executors/NodeConfig.cs` — typed config records
- `ILD.Data/Entities/LoopRun.cs` — run entity
- `ILD.Data/Entities/LoopRunNode.cs` — per-execution record
- `ILD.Data/Entities/LoopRunEdgeTraversal.cs` — edge traversal counts

### Current flow

1. Engine loads run state (LoopRun, WorkItem, template graph)
2. Engine resolves resume point (where to start after crash/recovery)
3. Engine creates `LoopRunNode` row before calling executor
4. Engine calls `executor.ExecuteAsync(ctx)` with pre-built `NodeExecutionContext`
5. Executor returns `NodeOutcome` (Succeeded, Failed, Suspended, Terminal, Throttled)
6. Engine updates `LoopRunNode`, writes event log, fires SignalR notifications
7. Engine routes to next node based on outcome + graph edges
8. Engine wraps execution in retry loop (`maxRetries`)
9. Engine pre-checks AI provider capacity before creating run node

### Current `NodeExecutionContext`

```csharp
NodeExecutionContext(
    LoopRun Run,
    LoopRunNode RunNode,
    LoopNode Node,
    WorkItemView WorkItem,
    string? PreviousNodeOutput,
    CancellationToken CancellationToken,
    Func<string, Task>? ProgressCallback
)
```

### Current `NodeOutcome` variants

- `Succeeded(output, resolvedPrompt)` — success, engine follows OnSuccess edge
- `Failed(reason, output)` — failure, engine retries or follows OnFailure edge
- `Suspended(reason, kind, output, resolvedPrompt)` — park run (HumanInput or ExternalSignal)
- `Terminal(output)` — run complete (Cleanup node)
- `Throttled(reason, providerId)` — provider at capacity, park run

---

## Final Target Architecture (Decisions Made)

### 1. Node Interface — `IAsyncEnumerable<NodeOutcome>`

Nodes become stateful generators that stream outcomes. Fresh calls only happen on park/resume cycles.

```csharp
interface INodeExecutor
{
    NodeType NodeType { get; }
    IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx);
}
```

### 2. Slim `NodeExecutionContext`

Engine passes minimal context. Node resolves its own dependencies via `IServiceProvider`.

```csharp
NodeExecutionContext(
    LoopRun Run,           // read state from here (CurrentNodeId, PreviousNodeOutput, ExternalActionResult, etc.)
    LoopNode Node,         // read config from here (Node.Config JSON)
    CancellationToken,
    Func<string, Task>? ProgressCallback
)
```

The node fetches `WorkItem` from the store using `Run.WorkItemId`.
The node reads `PreviousNodeOutput` from `Run.PreviousNodeOutput`.

### 3. New Outcome Types

| Outcome                          | Meaning               | Engine Action                                               |
| -------------------------------- | --------------------- | ----------------------------------------------------------- |
| `NodeStarting(effectiveInput)`   | Ready to commit       | Create `LoopRunNode` row with `EffectiveInput`              |
| `Success(EdgeType, output)`      | Done, exit via edge   | Update run node, write `PreviousNodeOutput`, route          |
| `Fail(EdgeType, reason, output)` | Failed, exit via edge | Same as Success                                             |
| `WaitingAction(reason, output)`  | Need external input   | Park run (`WaitingHuman`), park work item (`HumanFeedback`) |
| `WaitingIld(reason)`             | Provider at capacity  | Park work item (`WaitingForIld`), run stays `Running`       |
| `Terminal(output)`               | Run complete          | Update run node, complete run                               |

**Key differences from today:**

- `Suspended` renamed/split into `WaitingAction` (covers both human input AND PR webhook)
- `Throttled` renamed to `WaitingIld`
- `Success` and `Fail` now carry `EdgeType` (node decides which edge to exit via)
- `NodeStarting` is new — signals engine to create the `LoopRunNode` row

### 4. `LoopRun` as Source of Truth

New/changed fields on `LoopRun`:

| Field                          | Type      | Purpose                                                                                |
| ------------------------------ | --------- | -------------------------------------------------------------------------------------- |
| `PreviousNodeOutput`           | `string?` | Output from the node on the incoming edge (replaces in-memory `outputBySource`)        |
| `ExternalActionResult`         | `string?` | Human response or webhook signal (replaces per-`LoopRunNode` status for waiting state) |
| `ExternalActionResultRejected` | `bool`    | Whether the response was a rejection                                                   |

The engine writes `PreviousNodeOutput` after each node completes (before calling next node).
The engine writes `ExternalActionResult` when a human responds or webhook fires.
The node reads these fields on re-entry after a park/resume.

### 5. `LoopRunNode` Changes

| Field            | Change                                                                                                      |
| ---------------- | ----------------------------------------------------------------------------------------------------------- |
| `PreviousNodeId` | **NEW** — nullable Guid, links to the previous run node's ID. Enables retry-from-node by walking the chain. |
| `Status`         | **NEW value:** `Interrupted` — stale `Running` nodes on crash recovery transition to this                   |
| `RetryCount`     | **REMOVE** — no more automatic retries                                                                      |

### 6. Removed

- **`LoopRunEdgeTraversal` table** — frontend already derives edge arrows from consecutive `LoopRunNode` statuses. Engine enforces `maxTraversals` by counting in-memory.
- **`DescribeInput` on `INodeExecutor`** — replaced by `NodeStarting` carrying `EffectiveInput`
- **Automatic retries** — if a node fails, it fails. Graph designers model retry with `OnFailure` edges back to the same node + `maxTraversals`
- **Engine pre-check for AI throttling** — node checks capacity itself as first thing in `ExecuteAsync`

### 7. Engine Keeps

- Loading run state (LoopRun, template graph)
- Iterating the `IAsyncEnumerable` from each node
- Creating/updating `LoopRunNode` rows (on `NodeStarting` and final outcome)
- Writing `PreviousNodeOutput` to `LoopRun`
- Routing to next node (given `EdgeType`, find the edge, validate it exists)
- Run/work-item status transitions
- Crash recovery (transition stale `Running` → `Interrupted`, re-enter loop)
- SignalR notifications, event log writes
- `CleanupRunAsync` — dedicated external cleanup method
- `RetryFromNodeAsync` — rewinds using `PreviousNodeId` chain

### 8. Engine Loop Structure

```csharp
while (current != null)
{
    var executor = _registry.Get(current.NodeType);
    var ctx = new NodeExecutionContext(run, current, ct, progressCallback);

    bool routed = false;
    await foreach (var outcome in executor.ExecuteAsync(ctx))
    {
        switch (outcome)
        {
            case WaitingIld w:
                await parkWorkItem(..., WaitingForIld);
                return;  // engine thread exits

            case WaitingAction w:
                run.ExternalActionResult = w.Output;
                await parkRun(runId, WaitingHuman);
                return;  // engine thread exits

            case NodeStarting ns:
                await createRunNode(runId, current, ns.EffectiveInput);
                break;

            case Success s:
                await completeRunNode(runNode, Succeeded, s.Output);
                run.PreviousNodeOutput = s.Output;
                current = routeToNext(current, s.Edge);
                routed = true;
                break;

            case Fail f:
                await completeRunNode(runNode, Failed, f.Output);
                run.PreviousNodeOutput = f.Output;
                current = routeToNext(current, f.Edge);
                routed = true;
                break;

            case Terminal t:
                await completeRunNode(runNode, Succeeded, t.Output);
                await completeRun(runId);
                return;
        }
    }
    if (!routed) break;
}
```

### 9. Node Execution Pattern (Example: AI Node)

```csharp
public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
{
    // 1. Check preconditions
    var provider = await resolveProvider(ctx.Run, ctx.Node);
    if (!tracker.HasCapacity(provider.Id, provider.Parallelism))
        yield return new WaitingIld($"Provider at capacity");

    // 2. Ready to commit
    yield return new NodeStarting(DescribeInput(ctx));

    // 3. Do the work
    using tracker.Enter(provider.Id)
    {
        var result = await adapter.ExecuteAsync(...);
        yield return result.Success
            ? new Success(EdgeType.OnSuccess, result.Output)
            : new Fail(EdgeType.OnFailure, result.Error, result.Output);
    }
}
```

### 10. Human Node Pattern

```csharp
public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
{
    // On first call: no response yet
    if (string.IsNullOrEmpty(ctx.Run.ExternalActionResult))
        yield return new WaitingAction("Human input needed", renderedPrompt);

    // On re-entry after human response
    yield return new NodeStarting(DescribeInput(ctx));

    var response = ctx.Run.ExternalActionResult;
    var rejected = ctx.Run.ExternalActionResultRejected;

    if (rejected)
        yield return new Fail(EdgeType.OnFailure, "Human rejected");
    else
        yield return new Success(EdgeType.OnRespond, response);
}
```

### 11. PR Node Pattern

Same as Human node — yields `WaitingAction` on first call, reads `ExternalActionResult` on re-entry. Webhook handler writes to `ExternalActionResult`.

### 12. Crash Recovery

On resume:

1. Find stale `LoopRunNode` with `Status = Running`
2. Transition it to `Interrupted`
3. Re-enter engine loop at `CurrentNodeId`
4. Node calls `ExecuteAsync` fresh, starts from precondition checks

### 13. External Cleanup

Dedicated `CleanupRunAsync(runId)` method on engine:

1. Find Cleanup node in graph
2. Execute it via the node executor
3. Transition work item to Done/Backlog based on outcome

---

## Implementation Approach

### Phase 1: Data Model Changes

1. Add `PreviousNodeOutput`, `ExternalActionResult`, `ExternalActionResultRejected` to `LoopRun`
2. Add `PreviousNodeId` to `LoopRunNode`
3. Add `Interrupted` to `LoopRunNodeStatus` enum
4. Remove `RetryCount` from `LoopRunNode`
5. Delete `LoopRunEdgeTraversal` entity and table
6. Scaffold EF Core migration

### Phase 2: New Outcome Types

1. Rewrite `NodeOutcome` discriminated union with new variants
2. Remove `DescribeInput` from `INodeExecutor`
3. Change `ExecuteAsync` return type to `IAsyncEnumerable<NodeOutcome>`

### Phase 3: Rewrite Node Executors

1. Rewrite each executor (Start, Cmd, AI, Human, Prompt, PR, Cleanup) as `IAsyncEnumerable`
2. Move precondition checks into nodes
3. Remove engine-side pre-checks

### Phase 4: Rewrite Engine Loop

1. Rewrite `ExecuteRunLoopAsync` to iterate `IAsyncEnumerable`
2. Rewrite `ExecuteNodeWithRetryAsync` (no more retry loop)
3. Rewrite routing to use `EdgeType` from outcomes
4. Add `CleanupRunAsync` method
5. Rewrite `SignalNodeResultAsync` to write to `LoopRun.ExternalActionResult`
6. Rewrite `RetryFromNodeAsync` to use `PreviousNodeId` chain

### Phase 5: Frontend Updates

1. Update `LoopRunNode` type to include `previousNodeId`, `interrupted` status
2. Remove `retryCount` from type
3. Update NodeTimeline to handle `Interrupted` status
4. Update SignalR event types

### Phase 6: Tests

1. Rewrite unit tests for node executors (async enumerable pattern)
2. Rewrite engine integration tests
3. Add tests for crash recovery (Running → Interrupted)
4. Add tests for cleanup from external trigger

---

## Key Design Principles

1. **Node is pure I/O** — takes context, yields outcomes. Never mutates state directly.
2. **Engine is the state machine** — handles all persistence, routing, transitions.
3. **LoopRun is the source of truth** — all run state lives here.
4. **No automatic retries** — retry is modeled via graph topology (OnFailure edges + maxTraversals).
5. **NodeStarting is the commit point** — no `LoopRunNode` row until the node says it's ready.
6. **WaitingIld vs WaitingAction** — internal throttling (auto-resume) vs external waiting (human/webhook).

---

## Files to Modify

### Core (heavy changes)

- `ILD.Core/Services/Interfaces/INodeExecutor.cs` — new interface, new outcome types
- `ILD.Core/Services/Implementations/LoopEngine.cs` — complete rewrite of execution loop
- `ILD.Core/Services/Implementations/Executors/NodeExecutors.cs` — all executors rewritten
- `ILD.Core/Services/Implementations/Executors/PRNodeExecutor.cs` — rewritten
- `ILD.Core/Services/Implementations/Executors/NodeConfig.cs` — minor (no changes needed)
- `ILD.Core/Services/Implementations/NodeExecutorRegistry.cs` — no changes

### Data

- `ILD.Data/Entities/LoopRun.cs` — new fields
- `ILD.Data/Entities/LoopRunNode.cs` — new field, new status
- `ILD.Data/Entities/LoopRunEdgeTraversal.cs` — delete
- `ILD.Data/Enums/LoopRunNodeStatus.cs` — add Interrupted, remove Responded? (merge into WaitingHuman/Succeeded)
- `ILD.Data/Stores/Interfaces/ILoopRunStore.cs` — remove edge traversal methods
- `ILD.Data/Stores/LoopRunStore.cs` — remove edge traversal methods

### Recovery

- `ILD.Core/Services/Implementations/RecoveryManager.cs` — update crash recovery logic

### Frontend

- `frontend/src/types/index.ts` — update types
- `frontend/src/components/NodeTimeline/NodeTimeline.tsx` — handle Interrupted status

### Tests

- `ILD.Tests/` — all relevant tests need updating

---

## Open Questions (None — all resolved during grill-me)

All design decisions have been made and recorded above. The next agent should proceed with implementation starting with Phase 1.
