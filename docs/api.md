# API Surface

All ILD application routes are under `/api/v1/...`. The `/metrics` endpoint is the one exception — it is served at the root.

## Selected ILD endpoints

| Method      | Route                                  | Purpose                                                                              |
| ----------- | -------------------------------------- | ------------------------------------------------------------------------------------ |
| `POST`      | `/api/v1/auth/login`                   | Create a session token                                                               |
| `POST`      | `/api/v1/auth/logout`                  | Revoke the current session                                                           |
| `GET`       | `/api/v1/auth/me`                      | Return the authenticated user                                                        |
| `GET`       | `/api/v1/health`                       | Database and disk health summary                                                     |
| `PUT`       | `/api/v1/logging/level`                | Change backend log level at runtime                                                  |
| `GET`       | `/metrics`                             | Prometheus-style metrics snapshot (root route)                                       |
| `GET`       | `/api/v1/workitems`                    | List work items                                                                      |
| `POST`      | `/api/v1/workitems/{id}/transition`    | Transition a work item (e.g. claim to `Running`)                                     |
| `POST`      | `/api/v1/workitems/{id}/preview/start` | Start a QA preview                                                                   |
| `GET`       | `/api/v1/loopruns`                     | List loop runs                                                                       |
| `GET`       | `/api/v1/loopruns/{id}/events`         | Read run event logs (cursor-paginated)                                               |
| `GET`       | `/api/v1/remoteproviders`              | Manage git provider settings                                                         |
| `GET`/`PUT` | `/api/v1/workitemserver`               | Read/update the global WorkItem Server connection (URL, API key, poll/grace cadence) |
| `GET`       | `/api/v1/agent/workitems`              | Agent-facing work-item listing                                                       |

Manual start of a `Ready` work item is performed through `POST /api/v1/workitems/{id}/transition` with a target status of `Running` — there is no separate `/start` route. List endpoints accept `skip`/`take` (default 100, max 500); event-log queries cap `limit` server-side at 500.

## SignalR hubs

- `/hubs/loop-run`
- `/hubs/work-item`

## Standalone WorkItem Server endpoints

| Method | Route                        | Purpose                                           |
| ------ | ---------------------------- | ------------------------------------------------- |
| `GET`  | `/health`                    | Basic service liveness                            |
| `GET`  | `/workitems`                 | List work items                                   |
| `GET`  | `/workitems/poll`            | Heartbeat + ready-item polling                    |
| `POST` | `/workitems/{id}/transition` | Atomic claim or permissive state change           |
| `POST` | `/workitems/{id}/feedback`   | Append human feedback and move to `WaitingForIld` |

The WorkItem Server accepts API keys via either `Authorization: Bearer <key>` or an `X-Api-Key: <key>` header.

## Versioning

API routes use a manual `/api/v1/...` prefix hard-coded in `[Route]` attributes; there is no `Asp.Versioning` wiring. Breaking changes require introducing a new prefix (`/api/v2/...`) and keeping `/api/v1/...` working until clients migrate. See [CONTEXT.md](../CONTEXT.md#api-versioning-policy) for details.
