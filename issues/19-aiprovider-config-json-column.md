## Parent

PRD.md

## Status

**READY**

## What to build

Add a free-form JSON `Config` column to the `AiProvider` entity to store adapter-specific configuration. The existing typed fields (`BaseUrl`, `ApiKey`, `Model`) remain for backward compatibility with OpenAI-compatible providers. New adapter types (Opencode, Pi, etc.) read their configuration from the `Config` JSON blob.

The `AiProvider.Type` field drives adapter selection. The `Config` blob is opaque to the engine — only the matching adapter interprets it.

EF Core migration adds the column with a default of `{}`. Existing providers are unaffected.

## Acceptance criteria

- [ ] `AiProvider` entity has a new `Config` property of type `string?` (JSON)
- [ ] EF Core migration adds the column with nullable/default empty JSON
- [ ] `AiProviderDto` includes the `Config` field for API serialization
- [ ] `IProviderStore` and `ProviderStore` read/write the `Config` field through CRUD operations
- [ ] API endpoints (`POST /api/v1/aiproviders`, `PUT /api/v1/aiproviders/{id}`) accept and return the `Config` JSON
- [ ] Existing providers without `Config` continue to work (backward compatible)
- [ ] Backend tests cover: create provider with config, update provider config, read provider returns config, missing config defaults to empty
- [ ] `vp check` and `vp test` pass

## Blocked by

None - can start immediately
