# ADR-0031: The agent holds a placeholder; the ILD model gateway adds the real credential

An agent CLI used to run with the provider's login on disk where the agent uid could read and rewrite it ([ADR-0014](./0014-agent-uid-isolation.md) shared the credential store between the two uids), and with the model hosts open to it ([ADR-0019](./0019-agent-egress-through-in-container-proxy.md)). A prompt-injected agent could therefore copy an operator's Claude OAuth token, or plant a poisoned login for the next run. This ADR keeps provider credentials where only the orchestrator can read them.

## Decision

- **A gateway in ILD, no TLS interception.** Each CLI is pointed at `http://<ILD>/gateway/<route>/…` and given a placeholder in place of its token or key. The gateway checks the run's token, replaces the placeholder in `Authorization`, `x-api-key` or `api-key` with the real credential, and forwards over a TLS connection ILD opens itself. The response streams back unbuffered (server-sent events reach the client as the provider writes them).
- **One token per execution.** The registry mints a random token when an agent execution starts (a loop node, a chat turn, a chat title) and ends it when the execution ends or ILD restarts. It is bound to one provider. An unknown token is refused with 401, one whose execution has ended with 403. The token is the placeholder itself, so it travels in a header and never in a URL, where request logs would keep it. Codex also gets its own `CODEX_HOME` per run whose login holds the token as its access token.
- **Real credentials never leave the orchestrator.** They are read at request time from the credential store (`/home/ild/.agent-config`, now `0700`, no shared group, no ACL) or from the provider record (encrypted). The agent's own CLI directories hold sessions and settings, seeded once from the store without credential files.
- **Logins are made in a throwaway home.** The provider terminal still runs as the agent, but with a `HOME` that is discarded afterwards. When it closes, ILD imports a fixed list of login files into the store, reading each as the agent and never through a link, and only when the file parses and holds a login.
- **The model hosts are closed to the agent** while it holds a lease for that provider, in every network mode the proxy sees. This is defence in depth: the agent holds nothing a model host would accept.
- **The UI shows the path** each provider takes, from the adapter: `gateway` or `read-only-copy`, and nothing when the adapter does not say.

## Per provider

| Provider | Credential ILD adds | How the CLI is pointed at the gateway |
|---|---|---|
| Claude Code | The long-lived token `claude setup-token` prints, saved encrypted on the provider (or an API key). Until one is saved, the stored login's access token while it is valid. | `ANTHROPIC_BASE_URL`, a placeholder `CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_API_KEY`, `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` |
| Codex (ChatGPT) | The stored login's access token and account. ILD refreshes it itself (see below). | A provider of its own with `supports_websockets=false`, so Codex uses streamed HTTP; a placeholder `auth.json` in a per-run `CODEX_HOME` |
| Copilot | The GitHub token of the stored login | `GH_HOST` set to the gateway's loopback HTTPS listener, a `gho_`-shaped placeholder, `NODE_EXTRA_CA_CERTS`, `--no-auto-update` |
| OpenCode, Pi (base URL and key) | The provider's key | The gateway URL as `baseURL`, a placeholder key |
| Pi (built-in providers and its own `/login`) | Not covered, so a **read-only copy** | A per-run agent directory holding a `0400` copy of Pi's `auth.json` |

The matrix behind these rows, with what was run live, is `docs/run-hosts/provider-gateway-matrix.md`.

## Considered options

- **TLS interception (a CA the agent trusts, as the devvm does).** Rejected. It puts a signing key in the container and makes ILD a man in the middle of every TLS connection the agent makes, to protect one header on a few hosts. The CLIs all accept a base URL, so a direct hop needs no certificate trust at all. The one certificate involved is ILD's own server certificate for the Copilot listener, trusted by that CLI alone through `NODE_EXTRA_CA_CERTS`; it signs nothing else.
- **Refresh in the CLI.** Rejected for Codex. A CLI that refreshes needs the refresh token and a writable store. ILD refreshes the ChatGPT login itself when `last_refresh` is more than eight days old, when the access token is within a day of expiry, or after the provider refused it, and writes the result back atomically (`0600`, rename). The refresh token is only ever sent to the issuer from ILD. A request the provider refuses is not replayed (its body has been sent); the next one carries the renewed token.
- **Claude's stored OAuth login.** Its access token lasts hours and the CLI refreshes it with a refresh token. ILD does not refresh Claude logins, so the gateway uses the stored access token only while it is valid and otherwise asks for a long-lived token. An existing Claude provider has to open its terminal once to make one; the provider page says so.
- **Token in the URL path.** Rejected: request logs would keep it.

## Consequences

- **The read-only copy is a residual risk.** A provider the gateway cannot cover (Pi's own logins) gets a copy the agent can read, so a prompt-injected agent can still leak it; it can no longer change the shared store or read other runs' logins. A Pi key given as a CLI argument is visible in the process list, as before.
- **A leaked placeholder is worth nothing after its execution ends.** During it, an agent can ask the gateway for the provider's model, which it could always do; it cannot take the credential away, and the gateway answers only the routes above (for Copilot, only `copilot_internal/` and `*.githubcopilot.com` endpoints).
- **Gateway traffic is not accounted.** Cost and usage still come from each CLI's own output. Counting them from gateway traffic is a follow-up.
- **A gateway outage looks like a hang.** Codex in particular reports "Reconnecting…" rather than an error.
- **The Copilot listener needs a port.** `ILD_GATEWAY_TLS_PORT` (default 8442, loopback only); `0` switches it off, and Copilot runs then fail with a message that says so.
- **Network mode Off launches bypass the proxy**, so the host block does not apply to them; they hold only a placeholder either way.
- **Logins made before this change still work** for Codex, Copilot and OpenCode/Pi keys, which are read from the store or provider. Claude needs its one-time token.
