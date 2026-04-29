## Parent

PRD.md

## Status

**PENDING**

## What to build

Refactor the existing `AIProviderService` into an `OpenAiCompatibleAdapter` that implements `IAgentAdapter`. This adapter handles all OpenAI-compatible `/chat/compts` providers (OpenAI, Anthropic via proxy, local models, etc.).

The adapter reads provider config from `AiProvider.Config` JSON for adapter-specific settings, falls back to typed fields (`BaseUrl`, `ApiKey`, `Model`) for backward compatibility. It handles prompt rendering, HTTP call to the LLM endpoint, and returns the response.

This is a pure refactor — behavior is unchanged. The existing `CompleteAsync`, `RenderPromptAsync`, and `ExecuteToolAsync` logic moves into the adapter.

## Acceptance criteria

- [ ] `OpenAiCompatibleAdapter` class exists in `ILD.Core/Services/Implementations/Adapters/`
- [ ] Adapter implements `IAgentAdapter` with `SupportedProviderTypes = ["openai"]`
- [ ] Adapter reads provider config from `AiProvider.Config` JSON, falls back to `BaseUrl`/`ApiKey`/`Model` typed fields
- [ ] Adapter renders prompt template with `AgentExecutionContext` placeholders
- [ ] Adapter makes HTTP POST to provider's `/chat/completions` endpoint
- [ ] Adapter returns LLM response as `NodeExecutionResult`
- [ ] `IAIProviderService` is no longer called by `AINodeExecutor` (will be removed in #21)
- [ ] Backend tests cover: prompt rendering, HTTP call with mock handler, provider config fallback, tool execution within adapter
- [ ] `vp check` and `vp test` pass

## Blocked by

- Blocked by #18 (Adapter core and registry must exist)
- Blocked by #19 (Config JSON column must exist)
