## Parent

PRD.md

## Status

**READY**

## What to build

Define the `IAgentAdapter` interface and a DI-based auto-registration registry. The adapter is the core abstraction that replaces the monolithic `IAIProviderService` for AI node execution. Each adapter declares which provider types it handles via `SupportedProviderTypes`, and the registry maps `AiProvider.Type` → adapter at startup.

The adapter lifecycle is scoped per AI node per `LoopRun` — each AI node in a loop gets its own isolated adapter instance for the run's lifetime. Sibling AI nodes in the same loop do not share state.

Interface:

```csharp
interface IAgentAdapter {
    string Name { get; }
    string[] SupportedProviderTypes { get; }
    Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx);
}
```

Registry:

```csharp
interface IAgentAdapterRegistry {
    IAgentAdapter ResolveForProvider(AiProvider provider);
}
```

## Acceptance criteria

- [ ] `IAgentAdapter` interface exists in `ILD.Core/Services/Interfaces/`
- [ ] `AgentExecutionContext` record exists in `ILD.Data/DTOs/` with fields: `AiProvider Provider`, `string InitialPrompt`, `string LoopPrompt`, `LoopRunContext RunContext`, `int ExecutionCount`, `CancellationToken Cancel`
- [ ] `IAgentAdapterRegistry` interface exists with `ResolveForProvider` method
- [ ] `AgentAdapterRegistry` implementation auto-registers all `IAgentAdapter` via DI service descriptor enumeration
- [ ] Registry throws `InvalidOperationException` if no adapter matches a provider type
- [ ] Adapter instances are scoped per (LoopRunId, LoopNodeId) — not shared across sibling AI nodes
- [ ] Backend tests cover: registry resolution by type, multiple adapters registered, mismatched type throws, per-node-isolation of adapter instances
- [ ] `vp check` and `vp test` pass

## Blocked by

None - can start immediately
