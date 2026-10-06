# ADR-0024: Pi gets ILD tools from its own MCP client, registered by a generated extension

Before Pi 1.0, Pi had no MCP client, so ILD shipped one: a generated `ild.ts` extension loaded with `-e` imported an embedded `ild-mcp-bridge.js` that started the ILD MCP server, listed its tools and registered each as `ild_<name>`. Pi 1.0 has its own MCP client. We decided to keep the generated, per-run `ild.ts` (per chat session for a chat turn) in the agent read root, but reduce it to a single `pi.registerMcpServer("ild", …)` call carrying the server's command, args and env with `exposure: "direct"` and `timeout: 120`, and to delete the bridge. Pi now starts the server itself and exposes its tools as `mcp__ild__<name>`; the node's allowlist still reaches Pi as explicit names in `--tools`, read from the server DLL, so a node gets exactly the ILD tools it was given.

The file still carries the ILD API token, so it is written, owned and removed exactly as before (ADR-0014): orchestrator-owned, agent-readable, not agent-writable, deleted by run reclaim, chat delete and the startup sweep. The `ild-pi-ext` handling in the image, the entrypoint, `RunReclaimer` and `AgentIsolation` therefore stays.

## Env values are escaped

Pi resolves every registered MCP env value as config: `$NAME` and `${NAME}` expand, and a value starting with `!` is run as a shell command. The bridge passed values literally, so the extension writes each one escaped — every `$` becomes `$$`, and a value that then starts with `!` gets a `$` in front — which Pi reads back as the original text. This escaping is part of the contract with Pi: dropping it would let a token containing `$` change, or one starting with `!` run as a command. Command and args are not resolved (only a leading `~/` expands), so they are written as they are.

## Accepted trade-offs

- **A failed MCP start is not reported on stderr.** The bridge printed a line when the server did not start within 10 s. Pi waits up to 10 s for a direct server before the first prompt and then continues without its tools, but in `--mode json` its notices go nowhere ILD reads. The run still completes on the built-in tools.
- **Large results are middle-truncated.** Pi cuts a tool result over 20 KB in the middle and keeps the full text in a temp file the agent can read, where the bridge kept the head within Pi's line and byte limits.
- **An `ild` entry in a user's own Pi `mcp.json` takes precedence** over the registration. ILD never writes one; a user who adds one has chosen to replace ILD's server.

## Considered options

**Write the server into Pi's `mcp.json` instead of an extension.** Rejected: Pi reads MCP config only from its agent directory, a trusted project `.pi/mcp.json`, or an extension. The agent directory is agent-writable, and without an absolute BaseUrl it is the shared `~/.pi/agent` that also holds the Pi login, so a token there would be neither per-run nor read-only. A project file would sit in the worktree, which the agent writes and the branch carries.

**Keep the bridge.** Rejected: it duplicated an MCP client Pi now ships, and its own tests, for no requirement Pi's client does not meet beyond the stderr report above.
