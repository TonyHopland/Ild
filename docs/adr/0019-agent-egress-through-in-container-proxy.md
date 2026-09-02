# ADR-0019: Agent egress is funnelled through an in-container proxy keyed on the agent uid

The operator can limit which hosts the coding agent may reach — a whitelist, a
blacklist, a mode toggle (`off` / `whitelist` / `blacklist`, default `off`) and a
log of every destination — and that limit is enforced **inside the container,
keyed on the agent uid** (`agent`, 10002, from [ADR-0014](./0014-agent-uid-isolation.md)),
not at the network fabric. It is the same boundary the filesystem isolation
already draws, extended to the network, and it behaves the same under Docker,
Podman and Kubernetes.

## Why not the network layer

The obvious place for egress control is outside the container: Kubernetes
`NetworkPolicy`, Docker networks, Podman's netavark. That is three
implementations with three feature sets — Kubernetes needs a CNI that enforces
policy, Docker has no domain-level egress control at all — and none of them can
name a _host_: they filter on IP, which breaks on CDNs and rotating addresses
and means re-resolving and reloading rules on every list edit. Worse, they would
constrain the whole pod, orchestrator included. The item this closes stalled on
exactly that shape.

## The design

Two parts, deliberately separate.

- **Policy, log and UI are plain app state.** The lists are rows in
  `NetworkPolicyEntries` (an `AppSetting` value is capped at 4096 characters, so
  they cannot live there), the log is `NetworkLogEntries`, the mode is the
  `network.mode` app setting. A list entry is a host pattern with a scope: an
  exact host (`api.example.com`) or a leading-dot suffix (`.example.com`, which
  also matches `example.com` itself; `*.example.com` is accepted as the same
  thing), either global or limited to one AI provider. Matching is
  case-insensitive and ignores a trailing dot; IP literals match exactly. In
  `whitelist` mode a connection is allowed iff some applicable whitelist entry
  matches; in `blacklist` mode it is blocked iff some applicable blacklist entry
  matches; `off` records the destination as `advisory` and lets it through.
  Entries of the list the mode is not using are ignored, not inverted.

- **Enforcement is one small piece of Linux in the image.** The orchestrator
  runs a filtering **forward proxy** on loopback (`EgressProxy`, listening on
  `ILD_NETWORK_PROXY_PORT`, 3128 in the image). It takes the hostname from the
  `CONNECT` target, from the `Host` header of a plain request, or from the TLS
  ClientHello SNI of a flow redirected to it, so nothing is decrypted and the
  log carries real hostnames rather than bare IPs. Every agent launch — the
  CLI adapters, the interactive provider terminal, and Worktree Previews, which
  run as the agent per [ADR-0016](./0016-preview-runs-as-the-agent.md) — is
  pointed at it through `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` (both spellings)
  set in `AgentIsolation.Route`/`RouteCommand`, the same seam that crosses to the
  agent uid, so no launch site can forget it. `NO_PROXY` keeps loopback direct
  for the MCP callback to ILD's own API. Orchestrator-side spawns
  (`DropInheritedCapabilities`) are not proxied.

  Then the container entrypoint, as root and just before its privilege drop,
  installs an nftables rule set (iptables as fallback) keyed on the agent uid:
  loopback and DNS are accepted, **everything else is dropped**. A connection
  that skips the proxy — an IP literal, a client that ignores `HTTP_PROXY`, git
  over SSH — goes nowhere, so no DNS interception is needed for robustness. The
  rules carry **no domains**; they only say "this uid leaves through loopback".
  The orchestrator uid is untouched and keeps full network access.

## Runtime editing without a restart

Because the firewall rule carries no domains, editing a list never touches the
firewall: it is a database write the proxy reads on the next connection. The
proxy caches the mode and lists for one second **and** is invalidated by every
edit through the API (the same call that broadcasts the change to the UI), so
the per-entry **Add to whitelist / Add to blacklist** buttons are a live
control. Open tunnels are tracked by hostname and re-judged on every policy
change; one that is now blocked is reset rather than allowed to finish.

This is the opposite of baking resolved IPs into firewall rules, which would
need a reload per edit and break on rotating addresses.

## Per-provider scope, and why per-run is not here

A launch made for an AI provider gets a proxy URL of the form
`http://provider:<providerId>@127.0.0.1:<port>`; the client sends that as
`Proxy-Authorization`, and the proxy reads the provider back out to apply its
scoped entries. A connection that carries no credentials is judged by the
global entries alone. This is the only attribution the proxy has: all runs
share one agent uid, so it cannot tell one run's connection from another's.
Per-run or per-repository lists need either the per-run-uid follow-up from
ADR-0014 or a per-run credential the agent's client presents, and are a later
layer.

## Graceful degradation

Installing the rules needs `NET_ADMIN`, spent once by the root entrypoint. Per
runtime that is one knob — `cap_add: [NET_ADMIN]` in compose (now set on the
`ild` service, guarded by the compose test), `--cap-add=NET_ADMIN` for
docker/podman, `securityContext.capabilities.add: [NET_ADMIN]` on Kubernetes.
The drop to `ild` sheds it, the agent never holds it, and
`RUNTIME_AMBIENT_CAPS` deliberately does not include it: the rules persist in
the network namespace after the capability is gone.

Where the operator will not grant it, the container still starts, in
**advisory** mode: agent launches are still pointed at the proxy, which still
logs every destination and applies the lists to proxy-honouring clients — but a
hostile agent can bypass it. The entrypoint reports which of the two it achieved
(`ILD_NETWORK_ENFORCEMENT` plus a reason), the API exposes it at
`GET /api/v1/network/status`, and the Settings page shows a banner, so the
degradation is never silent.

## Consequences

- **Only what honours a proxy works for the agent under enforcement.** git over
  SSH, raw TCP, QUIC, anything reading neither `HTTP_PROXY` nor `HTTPS_PROXY`
  fails with a dropped connection rather than an error message. The standard
  toolchain (git over HTTPS, curl, npm, pip, the agent CLIs) all honour it.
- **The proxy is the hostname oracle.** A tool that connects to an IP literal
  through the proxy is logged and judged by that literal; the lists can name IP
  literals exactly for that case.
- **Loopback is open, not just the proxy port.** The MCP server the agent CLI
  spawns must reach ILD's API, and previews are served on loopback; both are the
  agent's own. DNS is open to any resolver, which is a residual channel.
- **The proxy resolves and connects as the orchestrator.** The lists are the
  only thing standing between the agent and any host the orchestrator can
  reach, including compose-network neighbours — which the agent could already
  reach directly before this change. A blacklist entry closes any of them.
- **ADR-0014's "What this does not close" still stands.** Writable `/data/repos`
  git hooks and the shared credential store are ways to code execution as `ild`,
  and `ild` is not constrained by these rules. Egress filtering is a real
  boundary for the agent uid and a speed bump for anything that has already
  crossed to the orchestrator; closing those paths is the follow-up work named
  there, and container root can flush the rules, which is why the capability
  hardening (#109) landed first.
- **Single-uid deployments (`AGENT_USER=`) are advisory by construction.** There
  is no separate uid to key rules on; the proxy still runs and logs.
