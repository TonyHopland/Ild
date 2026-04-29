## Parent

PRD.md

## Status

**PENDING**

## What to build

Transform `AINodeExecutor` into a thin router that resolves the correct `IAgentAdapter` via `IAgentAdapterRegistry` based on the `AiProvider.Type` of the configured provider, then delegates execution to the adapter.

The executor reads the node's config JSON for provider selection, resolves the `AiProvider` from the store, asks the registry for the matching adapter, and calls `adapter.ExecuteAsync()`. The executor no longer calls `IAIProviderService` directly.

Adapter instance is scoped per (LoopRunId, LoopNodeId) so each AI node in a loop gets its own isolated instance.

## Acceptance criteria

- [ ] `AINodeExecutor` resolves `AiProvider` from config using existing provider resolution logic (ID → name → default → first)
- [ ] `AINodeExecutor` asks `IAgentAdapterRegistry.ResolveForProvider()` for the matching adapter
- [ ] `AINodeExecutor` creates an `AgentExecutionContext` with provider, prompts, run context, execution count, and cancellation token
- [ ] `AINodeExecutor` calls `adapter.ExecuteAsync(context)` and returns the result
- [ ] `IAIProviderService` is removed as a dependency of `AINodeExecutor`
- [ ] Adapter instance is scoped per (LoopRunId, LoopNodeId) — not cached across nodes
- [ ] Backend tests cover: provider resolution chain, adapter delegation, context construction, cancellation propagation
- [ ] `vp check` and `vp test` pass

## Blocked by

- Blocked by #20 (OpenAI-compatible adapter must exist as the default adapter)
