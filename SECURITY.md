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
  are cryptographically random. All API and SignalR endpoints require authentication
  except health, metrics, login, and the runtime log-level endpoint.
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
  `ILD_ALLOWED_ORIGINS`.

## Hardening checklist for deployments

- Set a strong `ILD_PASSWORD` and `WORKITEM_API_KEYS`.
- Set `ILD_SECRET_KEY` to enable encryption at rest, and back the key up.
- Restrict access to the PostgreSQL volume and the host network.
- Do not run ILD as a multi-tenant or publicly reachable service.
