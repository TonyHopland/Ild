# API Design

ILD exposes **two distinct HTTP surfaces**. Keeping them separate is deliberate — see [ADR-0001](./adr/0001-standalone-workitem-server.md).

| Surface                 | Host                 | Audience                                               | Auth                                                          |
| ----------------------- | -------------------- | ------------------------------------------------------ | ------------------------------------------------------------- |
| **ILD application API** | `ILD.Api`            | The SPA, operators, and the agent-facing MCP tooling   | Bearer-token session (cookie/header)                          |
| **WorkItem Server API** | `ILD.WorkItemServer` | ILD instances coordinating over shared work-item state | API key (`Authorization: Bearer <key>` or `X-Api-Key: <key>`) |

The ILD application API is mounted under `/api/v1/...`; the standalone WorkItem Server mounts its routes at the root (`/workitems`, `/health`). Realtime updates flow over SignalR, not REST.

## Conventions

- **Versioning.** Routes carry a hand-written `/api/v1` prefix rather than the `Asp.Versioning` package. Breaking changes introduce a new prefix and keep the old one alive until clients migrate — see [ADR-0002](./adr/0002-manual-api-versioning.md).
- **Auth scope.** `AuthMiddleware` enforces bearer auth only on `/api/*`, `/hubs/*`, and `/metrics`; SPA routes and static assets fall through so the shell can serve. A small `ExcludedPaths` set (login, health, logging, metrics) is exempt. Webhook routes are _not_ exempt — they additionally require HMAC verification on top of bearer auth. The `/metrics` endpoint is served at the root.
- **Pagination.** List endpoints accept `skip`/`take` (default 100, capped at 500). Event-log queries are cursor-paginated and cap `limit` at 500 server-side.
- **Errors from AI providers** surface as `AiProviderException` with cause-preserving inner exceptions.

A representative slice of the ILD API: `POST /api/v1/auth/login`, `GET /api/v1/workitems`, `POST /api/v1/workitems/{id}/transition` (manual start of a `Ready` item targets status `Running` — there is no separate `/start` route), `GET /api/v1/loopruns/{id}/events`. Controllers live under `ILD.Api/Controllers`; consult them for the exhaustive route list rather than duplicating it here.

## Realtime channel

Two SignalR hubs broadcast state changes, both emitting `{ type, payload, timestamp }`:

- `/hubs/loop-run` — run-level events
- `/hubs/work-item` — work-item-level events

Event payload types are statically modelled in `frontend/src/types/signalr.ts`. See [Architecture → Realtime channel](./architecture.md#realtime-channel) for the event catalogue.

## WorkItem Server API

The standalone server owns work-item state and claim semantics (see [ADR-0001](./adr/0001-standalone-workitem-server.md)). Its surface centres on listing and polling work items (`GET /workitems`, `GET /workitems/poll` for heartbeat + ready-item polling), atomic state changes (`POST /workitems/{id}/transition` for claim-or-permissive transitions), and human/agent dialogue (`POST /workitems/{id}/feedback` moves the item to `WaitingForIld`; `POST /workitems/{id}/conversation` appends a turn without changing status). `GET /health` reports liveness.

## See also

- [Architecture](./architecture.md) — module boundaries and the realtime channel
- [ADRs](./adr/) — the architectural decisions behind these conventions
- [CONTEXT.md](../CONTEXT.md) — glossary and the detailed auth/webhook enforcement model
