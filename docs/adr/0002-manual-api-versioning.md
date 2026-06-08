# ADR-0002: Manual `/api/v1` prefix instead of `Asp.Versioning`

API routes carry a hand-written `/api/v1` prefix in their `[Route]` attributes; there is no `Asp.Versioning` package wiring. The API surface is small and single-tenant, so the library's negotiation machinery (header/query/media-type versioning, version sets) wasn't worth the dependency and indirection. A future reader should not assume the absence of `Asp.Versioning` is an oversight — it is deliberate.

## Consequences

- Breaking changes require introducing a new prefix (`/api/v2/...`) and keeping `/api/v1/...` working until clients migrate. There is no automatic deprecation or version-discovery support.
