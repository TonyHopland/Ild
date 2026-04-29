## Parent

PRD.md

## Status

**PENDING**

## What to build

Add an API endpoint that returns the config schema for a given adapter type, and update the LoopEditor's AI node config panel to render fields dynamically based on the selected provider's adapter schema.

Each `IAgentAdapter` exposes a `ConfigSchema` property (JSON Schema or simple field descriptors). When the user selects an AI provider in the node config panel, the frontend fetches the adapter's schema and renders appropriate config fields.

The AI node config panel already exists from #08. This issue extends it to be schema-driven rather than hardcoded.

## Acceptance criteria

- [ ] `IAgentAdapter` has a `ConfigSchema` property returning field descriptors (name, type, label, required, default, description)
- [ ] API endpoint `GET /api/v1/agent-adapters/{providerType}/config-schema` returns the schema for the matching adapter
- [ ] LoopEditor AI node config panel fetches schema on provider selection change
- [ ] Config fields render dynamically based on schema (text input, number, toggle, textarea, select)
- [ ] Config values serialize to the `AiProvider.Config` JSON blob on save
- [ ] Backward-compatible: OpenAI-compatible adapter returns schema for existing fields (model, base URL, API key)
- [ ] Frontend tests cover: schema fetch on provider change, dynamic field rendering, config serialization to JSON
- [ ] Backend tests cover: schema endpoint returns valid schema, unknown provider type returns 404
- [ ] `vp check` and `vp test` pass

## Blocked by

- Blocked by #21 (Adapter pipeline must work end-to-end)
