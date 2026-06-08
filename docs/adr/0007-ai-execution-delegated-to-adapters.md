# ADR-0007: AI execution is delegated wholesale to pluggable CLI-backed adapters

An AI node does not orchestrate the model itself. It resolves an `IAgentAdapter` from `AiProvider.Type` and hands off the entire execution lifecycle — multi-turn loops, tool use, session state — to that adapter. The currently registered types (`opencode`, `pi`, `claude-code`) are all CLI-backed: the adapter spawns the provider's CLI inside the worktree and reads its structured output. We gave adapters this autonomy because each agent CLI already owns a sophisticated turn/tool loop; re-implementing that inside the engine would duplicate it badly and lag behind each tool's own capabilities.

## Consequences

- The engine treats an AI node as a single bounded step regardless of how many internal turns the adapter runs; the node exposes one `prompt` field, and prompt variation across turns is modelled with upstream Prompt nodes ([ADR-0005](./0005-template-resolved-per-run-from-tags.md) covers graph-driven routing generally).
- Adapter instances are scoped per AI node per LoopRun, so sibling AI nodes don't share state. There is no implicit default adapter type — a provider must map to a registered adapter.
- The engine passes a per-node tool allowlist, but the adapter decides how to enforce it against its own tool/permission system.
