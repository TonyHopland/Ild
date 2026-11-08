# Security Policy

## Reporting a vulnerability

Please report security issues **privately**. Do not open a public GitHub issue for a
suspected vulnerability.

Open a [GitHub private security advisory](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
on this repository: go to the **Security** tab → **Report a vulnerability**. This keeps
the report confidential and lets us coordinate a fix and disclosure with you.

Please include the affected component, reproduction steps, and the impact you observed.
We aim to acknowledge reports within a few days. As a small project there is no formal
SLA, but credible reports are taken seriously.

## Supported versions

This project is pre-1.0 and under active development. Only the latest `main` receives
security fixes. Pin to a commit you have reviewed for production-like use.

## Security model

ILD is designed to be **self-hosted by a trusted operator**, typically as a single-admin
instance. Understanding the trust boundaries matters when deploying it:

- **Authentication** is a single bootstrap user (`ILD_USERNAME` / `ILD_PASSWORD`). The
  password is stored hashed with PBKDF2-SHA256 (salted, 100k iterations); session tokens
  are cryptographically random and are stored only as a hash. **When
  `ILD_SESSION_TOKEN_PEPPER` is set** — which the compose stack requires — that hash is
  an HMAC-SHA256 keyed on it, so a session row cannot be forged by anything that can
  write the database without also holding the key. Without it, running from source, the
  hash is an unkeyed SHA-256 and anyone who can write the sessions table can mint a
  sign-in; startup warns when this is the case. All API and SignalR endpoints require
  authentication except health, metrics, login, and the runtime log-level endpoint.
- **The application executes commands and external agent CLIs by design.** `Cmd` nodes
  and preview commands in loop templates run shell commands inside per-item git worktrees,
  in the run's container or pod. Anyone who can author loop templates (i.e. the
  authenticated admin) can run arbitrary commands there, and through ILD's egress proxy
  reach whatever its network limits allow. **Do not expose ILD to untrusted users, and do
  not import loop templates from untrusted sources.**
- **Secrets at rest.** Provider API keys and webhook secrets are encrypted with
  AES-256-GCM when `ILD_SECRET_KEY` is set; otherwise they are stored in plaintext. See
  [docs/configuration.md](docs/configuration.md#secret-encryption-at-rest). Secret values
  are masked (`***`) in API responses.
- **Network exposure.** The container runs as a non-root user. Bind it behind a trusted
  network boundary or reverse proxy; CORS defaults to localhost and is configurable via
  `ILD_ALLOWED_ORIGINS`. PostgreSQL is not published to the host by the compose stack.
- **Agents run apart from ILD.** ILD's image runs every loop run, chat and provider
  sign-in in a container of its own, or a pod on Kubernetes
  ([ADR-0036](docs/adr/0036-runs-always-in-containers.md)), on a network that reaches
  only ILD: not PostgreSQL, the WorkItem Server or another run. The connection strings,
  `ILD_SECRET_KEY` and `ILD_SESSION_TOKEN_PEPPER` never reach an agent's environment, and
  the database passwords are per deployment rather than constants shipped in this
  repository. Add any new orchestrator secret to `ILD_AGENT_ENV_DENYLIST` or the built-in
  scrub list. ILD started with `dotnet run` runs agents as its own processes, with none of
  this isolation; it is for development only.
- **The container engine socket is root-equivalent on a rootful engine.** ILD holds the
  Docker or Podman socket it is given, and whoever holds it controls every container on
  that engine; no run or chat container is given it. Prefer rootless Podman, and see
  [docs/deployment.md](docs/deployment.md#socket-trust).

## Hardening checklist for deployments

- Set a strong `ILD_PASSWORD` and `WORKITEM_API_KEYS`.
- Set `ILD_SECRET_KEY` to enable encryption at rest, and back the key up.
- Set `ILD_SESSION_TOKEN_PEPPER`, and generate the three database passwords
  (`POSTGRES_PASSWORD`, `ILD_DB_PASSWORD`, `WORKITEM_DB_PASSWORD`) per deployment. The
  compose stack refuses to start without them; generate each with `openssl rand -hex 32`.
- Restrict access to the PostgreSQL volume and the host network, and leave the
  `postgres` service unpublished.
- Give ILD a rootless engine's socket where you can.
- Do not run ILD as a multi-tenant or publicly reachable service.
