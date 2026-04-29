## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Build frontend Settings pages for managing Remote Providers (Forgejo/GitHub/GitLab connections) and AI Providers (LLM configurations). Each page lists existing providers, allows creating and updating them with name, type, URL, API key, and model settings. Wire up to the existing `/api/v1/remoteproviders` and `/api/v1/aiproviders` endpoints.

## Acceptance criteria

- [x] Remote Provider page: list, create, update with fields (name, type, URL, API key, webhook secret)
- [x] AI Provider page: list, create, update with fields (name, type, base URL, API key, model)
- [x] API key fields are masked in the UI (not displayed in plaintext)
- [x] Form validation for required fields and URL format
- [x] SignalR or poll-based refresh after create/update
- [x] Frontend tests cover: form rendering, validation, API call on submit, list rendering, API key masking
- [x] `vp check` and `vp test` pass

## Blocked by

None - can start immediately (backend API endpoints already exist)
