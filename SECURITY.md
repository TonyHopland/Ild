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
  and preview commands in loop templates run shell commands inside per-item git worktrees.
  Anyone who can author loop templates (i.e. the authenticated admin) can run arbitrary
  commands on the host with the container's privileges. **Do not expose ILD to untrusted
  users, and do not import loop templates from untrusted sources.**
- **Secrets at rest.** Provider API keys and webhook secrets are encrypted with
  AES-256-GCM when `ILD_SECRET_KEY` is set; otherwise they are stored in plaintext. See
  [docs/configuration.md](docs/configuration.md#secret-encryption-at-rest). Secret values
  are masked (`***`) in API responses.
- **Network exposure.** The container runs as a non-root user. Bind it behind a trusted
  network boundary or reverse proxy; CORS defaults to localhost and is configurable via
  `ILD_ALLOWED_ORIGINS`. PostgreSQL is not published to the host by the compose stack.
- **The database is reachable from the agent uid.** The coding agent runs as a separate,
  lower-trust user ([ADR-0014](docs/adr/0014-agent-uid-isolation.md)), but it shares the
  container's network namespace, so it can open a TCP connection to PostgreSQL. What
  stops it is credentials, not reachability: the connection strings, `ILD_SECRET_KEY`
  and `ILD_SESSION_TOKEN_PEPPER` are stripped from the agent's environment, and the
  database passwords are per deployment rather than constants shipped in this
  repository. Treat every one of them as a boundary, and add any new orchestrator secret
  to that scrub list. This is the concrete counterpart to the shared-input routes ADR-0014
  records under _What this does not close_: the uid split does not make the agent a
  contained tenant, so each thing it could otherwise reach is closed by name.

## Hardening checklist for deployments

- Set a strong `ILD_PASSWORD` and `WORKITEM_API_KEYS`.
- Set `ILD_SECRET_KEY` to enable encryption at rest, and back the key up.
- Set `ILD_SESSION_TOKEN_PEPPER`, and generate the three database passwords
  (`POSTGRES_PASSWORD`, `ILD_DB_PASSWORD`, `WORKITEM_DB_PASSWORD`) per deployment. The
  compose stack refuses to start without them; generate each with `openssl rand -hex 32`.
- Restrict access to the PostgreSQL volume and the host network, and leave the
  `postgres` service unpublished.
- Do not run ILD as a multi-tenant or publicly reachable service.
