# ADR-0037: External agents authenticate with expiring, revocable grants

An AI session running outside ILD (Claude, ChatGPT or Codex somewhere else that can reach ILD's address) sometimes needs to use ILD's agent API or MCP tools. The operator gives it an **external-agent grant**: a name, a lifetime of at most seven days, and a token shown once. The token is accepted on `/api/v1/agent/*` and on the Streamable-HTTP MCP endpoint `/mcp`, and nowhere else.

The only way to do this before was a process-wide agent token handed out by hand. ILD no longer has one: it issues each run and chat its own token. Such a token would again be shared, not revocable per use, unattributable and non-expiring, and would let its holder name any run or chat in a header.

## Decision

- **A third principal, never the operator.** A grant token authenticates as the role `external-agent`, with the grant id as a claim. It is never the `user` role, so the user-only fallback policy refuses it (403) on every endpoint that does not name `AgentSurface`, which is the agent API and `/mcp` alone. Settings, AI providers, backup, sessions and the grant routes themselves are user-only.
- **Header only.** A grant token is read only from `Authorization: Bearer`. The `?access_token=` query form exists for WebSocket handshakes, and a URL ends up in logs and browser history, so a grant token found there is a 401 even when it is live.
- **Expiry is required and enforced on every request.** The lifetime is mandatory and capped at seven days server-side (a longer request is refused, not shortened). An expired or revoked grant fails authentication on its next request; nothing is cached. Only the keyed hash of the token is stored (`SessionTokenHasher`, as for sessions), and the plaintext is returned by the create call alone.
- **A grant is its own session.** The agent rules depend on a calling session: an agent edits and deletes only what its session created, and changes everything else through human-approved proposals ([ADR-0022](./0022-agent-edits-are-human-approved-proposals.md), [ADR-0027](./0027-agents-suggest-global-variable-changes.md), [ADR-0028](./0028-agents-propose-loop-template-changes.md), [ADR-0030](./0030-agents-suggest-human-input.md)). A grant is a third session source beside a loop run and a chat: items it creates carry `createdByExternalGrantId`, proposals it makes carry it too, and ownership checks compare it. `X-ILD-Run-Id` and `X-ILD-Chat-Session-Id` sent with a grant token are refused (403), so a grant can never act as another session. The routes that need a run or a chat (loop variables, the current loop, chat clones) answer 400 for a grant, and their MCP tools are not offered.
- **Agent-level rights, with a narrower reach than a chat.** A chat is driven by the operator and reaches any work item's worktree, preview, pull request and attachments; a grant is not driven by anyone present, so, like a run, it reaches those routes only for the items it created. It can read the backlog, create items (always into Backlog), edit and delete its own, and propose. It cannot start, transition or merge work.
- **Read-only grants.** A grant may be created read-only: every non-GET request is refused (403), which includes creating items and making proposals.
- **Attribution.** The change history records a grant's changes with actor kind `ExternalAgent` and the grant's name and id as the detail. The WorkItem Server's actor kinds gain the same value.
- **Pending proposals outlive the grant.** Revoking or expiring a grant neither rejects nor hides its pending proposals: they stay pending and decidable, on the edited item's Overview as today and on the grant's page, which lists them until a human decides. Nothing about deciding a proposal depends on its proposer being live.
- **Audit and limits.** Every request a grant token authenticates is logged and stored (method, path without the query string, status, caller address), shown as recent activity on the grant's page, and stamps the grant's last use. Each grant has its own request budget per minute (429 when spent), so one runaway agent cannot starve the operator or another grant.
- **MCP transport: in-process, looping back with the caller's token.** `/mcp` hosts the tool classes of `ILD.McpServer` (the same ones ILD's own agents get over stdio) through `ModelContextProtocol.AspNetCore`, stateless so each request authenticates by itself. A tool call reaches the agent API by an HTTP request to this process carrying the caller's own bearer token. That keeps every rule the REST surface enforces (ownership, read-only, proposals, rate limit, audit) in one place, with nothing duplicated into the tools; direct calls into services would have made each tool re-implement authorization the controller already does. The cost is one extra local request per tool call and a loopback address to know, taken from the address the API listens on (`Mcp:LoopbackUrl` overrides it).

## Considered options

- **Reuse the agent token mechanism** (`AgentToken` with a third source). Rejected: those tokens belong to a launch that ends, are issued by ILD itself and have no name, lifetime choice or operator-facing list. A grant is operator-created and long-lived by comparison.
- **OAuth for MCP.** Out of scope: a bearer header is what MCP clients and `curl` both support today.
- **Scopes beyond read-only/full.** Out of scope.
- **Direct service calls from the MCP tools.** Rejected, see above.

## Consequences

- Handing out a grant exposes the agent surface to whoever holds the token until it ends, so the lifetime is short by design and the operator can revoke one grant or all at once.
- A grant's pending proposals are never swept: a human who never decides them leaves them pending, as for a run or a chat that ended.
- Every request through `/mcp` counts twice against the grant's budget: once for the MCP call and once for each loopback call it makes.
