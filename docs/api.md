# API Design

ILD exposes **two distinct HTTP surfaces**. Keeping them separate is deliberate — see [ADR-0001](./adr/0001-standalone-workitem-server.md).

| Surface                 | Host                 | Audience                                               | Auth                                                          |
| ----------------------- | -------------------- | ------------------------------------------------------ | ------------------------------------------------------------- |
| **ILD application API** | `ILD.Api`            | The SPA, operators, and the agent-facing MCP tooling   | Bearer-token session (cookie/header)                          |
| **WorkItem Server API** | `ILD.WorkItemServer` | ILD instances coordinating over shared work-item state | API key (`Authorization: Bearer <key>` or `X-Api-Key: <key>`) |

The ILD application API is mounted under `/api/v1/...`; the standalone WorkItem Server mounts its routes at the root (`/workitems`, `/health`). Realtime updates flow over SignalR, not REST.

## Conventions

- **Versioning.** Routes carry a hand-written `/api/v1` prefix rather than the `Asp.Versioning` package. Breaking changes introduce a new prefix and keep the old one alive until clients migrate — see [ADR-0002](./adr/0002-manual-api-versioning.md).
- **Auth scope.** Bearer auth is an authentication scheme with a deny-by-default authorization policy: every endpoint requires an authenticated caller in the `user` role unless it opts out. Login, health, logging, metrics and the SPA fallback are anonymous; the agent surface (`/api/v1/agent/...`) and the webhook routes accept any authenticated caller, so an agent token reaching anything else is refused with 403. Webhook routes additionally require HMAC verification on top of bearer auth. Static assets are served before authorization runs, so the shell can load. The `/metrics` endpoint is served at the root.
- **Pagination.** List endpoints accept `skip`/`take` (default 100, capped at 500). Event-log queries are cursor-paginated and cap `limit` at 500 server-side.
- **Errors from AI providers** surface as `AiProviderException` with cause-preserving inner exceptions.
- **Advisory checks are not gates.** `GET /api/v1/workitems/branch-name-check?name=&repositoryId=&workItemId=` answers `{ error, warning }` for a work item's custom branch name while it is being typed. `error` (an illegal git ref) is also enforced on create and edit and will fail the save; `warning` (the name is already taken) never blocks one, because the authoritative check is the one the engine takes at run start — see [ADR-0008](./adr/0008-worktree-and-branch-per-run.md).
- **Sessions are plural.** `POST /api/v1/auth/login` mints one session per device and reports its `expiresAt` (null when the absolute cap is disabled); `POST /api/v1/auth/logout` revokes only the calling one. `GET /api/v1/auth/sessions` lists the caller's own live sessions — an opaque `id`, `createdAt`, `lastSeenAt`, `expiresAt`, `userAgent`, `createdFromIp`, and `isCurrent`, never a token or a token hash. `DELETE /api/v1/auth/sessions/{id}` revokes one (404 when it is not a live session of the caller); `POST /api/v1/auth/sessions/revoke-others` revokes every session but the calling one and answers `{ revoked }`.
- **Work item branch fields.** `branchNameOverride` (the branch a run uses) and `baseBranchOverride` (the branch it starts from and opens its PR against) are accepted on create and update, on both `/api/v1/workitems` and the agent surface, and returned on read. Both follow the same convention: omitted means "not part of this edit", an empty string clears the override back to the default. Both are validated as git ref names on save; whether a base branch actually _exists_ is not, because that is only answerable — and only binding — when the run starts.

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
