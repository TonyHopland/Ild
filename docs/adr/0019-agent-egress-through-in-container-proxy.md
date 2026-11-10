# ADR-0019: Agent egress is funnelled through an in-container proxy keyed on the agent uid

> **Status: superseded in part by [ADR-0036](./0036-runs-always-in-containers.md).** The uid-keyed firewall, the agent uid and `NET_ADMIN` are gone; the egress proxy, its lists and its log remain, and a run's or chat's network, which reaches only ILD, is what makes the proxy the only way out.

The operator can limit which hosts the coding agent may reach — a whitelist, a
blacklist, a mode toggle (`off` / `monitor` / `whitelist` / `blacklist`, default
`monitor`) and a log of every destination — and that limit is enforced **inside the container,
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
  matches; `monitor` records the destination as `advisory` and lets it through.
  Entries of the list the mode is not using are ignored, not inverted. The
  fourth mode, `off`, is not a proxy verdict at all: see [Off](#off-no-proxy-no-log).

- **Enforcement is one small piece of Linux in the image.** The orchestrator
  runs a filtering **forward proxy** on loopback (`EgressProxy`, listening on
  `ILD_NETWORK_PROXY_PORT`, 3128 in the image). It takes the hostname from the
  `CONNECT` target, from the `Host` header of a plain request, or from the TLS
  ClientHello SNI of a flow redirected to it, so nothing is decrypted and the
  log carries real hostnames rather than bare IPs. Every agent launch — the
  CLI adapters, the interactive provider terminal, and Worktree Previews, which
  run as the agent per [ADR-0016](./0016-preview-runs-as-the-agent.md) — is
  pointed at it through `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` (both spellings)
  plus `NODE_USE_ENV_PROXY=1`, without which Node's built-in `fetch` (corepack
  included) ignores the other three, all set in
  `AgentIsolation.Route`/`RouteCommand`, the same seam that crosses to the
  agent uid, so no launch site can forget it. `NO_PROXY` keeps loopback direct
  for the MCP callback to ILD's own API. Orchestrator-side spawns
  (`DropInheritedCapabilities`) are not proxied.

  Then the container entrypoint, as root and just before its privilege drop,
  installs an nftables rule set (iptables as fallback) keyed on the agent uid:
  loopback is accepted but DNS is not, **everything else is dropped**. A
  connection that skips the proxy — an IP literal, a client that ignores
  `HTTP_PROXY`, git over SSH — goes nowhere, so no DNS interception is needed
  for robustness. The rules carry **no domains**; they only say "this uid
  leaves through loopback".
  The orchestrator uid is untouched and keeps full network access.

## Off: no proxy, no log

> **In a container or pod** (ADR-0036) there is no uid firewall or open group: `off` instead
> gives the run's container or pod a direct route to the network (a second, non-internal
> network on Docker and Podman; a second, allow-all egress NetworkPolicy on Kubernetes) and
> its launches no proxy variables, so the sentence below holds there too. The route belongs to
> the whole container or pod, not to the process: every process in the run can use it while
> it is attached, one in a proxied mode launched earlier included. It is removed at the first
> launch in a proxied mode once no process launched in `off` is still running; chats share one
> container, so one chat in `off` opens it for all. Protected destinations do not apply to it.

Some tools cannot use the funnel: those that ignore `HTTP(S)_PROXY`, and
anything that is not HTTP (SSH, gRPC, UDP, QUIC). `off` lets them out. Its
agents connect directly, to any host and any port, and nothing is logged. Before
`monitor` existed `off` meant "log everything"; that mode is now `monitor`, and a
stored `off` is rewritten to `monitor` once at startup (`NetworkModeMonitorMigrator`)
so nobody loses their log by upgrading.

The firewall rules are installed once, as root, and the app has no `NET_ADMIN`,
so the app cannot change them when the mode changes. They therefore tell the
modes apart by something the app _can_ choose per launch: the agent process's
**group**. Three groups are in play and must not be confused:

- the agent's **primary group** (`agent`), the `--regid` of every proxied launch;
- the **shared group** (`ild-agents`), a supplementary group both users are in,
  loaded by `--init-groups`, never a `--regid`;
- the **open group** (`ild-net-open`, `NETWORK_OPEN_GROUP`), the `--regid` of an
  Off launch only. It has no members.

The entrypoint adds one accept rule, for the agent uid _and_ the open gid, right
after the "not the agent uid" accept and before the DNS drops, in both backends
(nftables: `meta skuid $uid meta skgid $gid accept`; iptables: `--uid-owner
$uid --gid-owner $gid`, effective gid only). Everything else the agent uid sends
is dropped as before. For each launch the app reads `network.mode`: in `off` it
sets none of the proxy variables and runs `setpriv --reuid=agent
--regid=<open group> --init-groups …`; in every other mode the launch is exactly
what it was. The entrypoint exports the group as `ILD_NETWORK_OPEN_GROUP` only
where it installed the rule, and the topology variables stripped from agent
processes include it.

Which launches follow the mode: the agent CLIs (runs, chats, chat titles), the
provider terminal, Codex custom MCP servers, Cmd nodes, and Worktree Preview
install steps and services (a start resolves the mode once). Git as the agent
(`ProcessRunner`, `RepositoryManager`), `ChatRepositoryCloner` and
`AgentWritableFiles` stay on the primary group with no proxy in every mode: they
only talk to the local mirror. A mode change applies to the _next launch_;
running processes keep their environment and gid and are not signalled. Proxied
processes that outlive a switch to `off`, and forwards, are judged as `monitor`
(allowed and logged).

**The agent cannot move itself into the open group.** It holds no capabilities
(`CAP_SETGID` stays with the orchestrator), is not a member of the group, the
group has no password, and `no_new_privs` is set. Only the orchestrator, which
chooses `--regid`, can start a process in it.

**The setgid-file route, and why `off` needs `no-new-privileges`.** A process
running with gid `ild-net-open` can create a file outside the setgid shared
directories (`/tmp`, the agent home, `~/.local/bin`), which is then group-owned by
the open group, and `chmod g+s` it. Without `no_new_privs`, any agent-uid
process, even one launched in `whitelist`, could execute it and come out with
the open gid: a way around the firewall. `no_new_privs` makes the kernel ignore
the setgid bit on exec, and it is inherited by every descendant and cannot be
cleared, so the route is closed for every agent process. That is a runtime
setting (compose sets it; podman, docker and Kubernetes must), not something ILD
can enforce itself, so the entrypoint enables `off` only when `NoNewPrivs` is 1
in its own status. Without it, or when the group is missing, equals the agent's
primary gid or the shared group's gid, or lists the agent as a member, it installs
no open-group rule, exports nothing, warns, and reports in the enforcement reason
that `off` is unavailable and agents launched in it have no network. Everything
else is still enforced.

The shared directories are setgid `ild-agents`, so files an Off agent creates
there keep the shared group. Elsewhere (agent home, scratch, `/tmp`) they get the
open group but stay agent-owned; nothing relies on the agent's primary group for
access, so switching modes between runs leaves nothing the next run cannot use.

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

## Launch credentials, and what scope they carry

Every launch (a run's agent or command, a chat, a toolchain installer, a provider sign-in) gets a proxy URL of the form
`http://ild:<token>@127.0.0.1:<port>`, and the client sends the token as `Proxy-Authorization`. The proxy **requires** it: a connection with no token, or one ILD did not sign, is refused with `407` (an `access_denied` alert for a redirected TLS flow, which has no header to carry one) and is logged as blocked. This also keeps everything else that can reach the proxy's port off it: other compose containers, other pods in a cluster, the LAN.

The token is signed, not stored: `base64url(scope).base64url(HMAC-SHA256(scope))`, with a 32-byte key ILD keeps at `<data path>/egress/proxy.key` (mode 0600, made on first start). The payload names the launch: its kind (run, chat, installer, sign-in or preview), the run or chat it was made for, the provider whose scoped entries apply, and for an installer its zone. Every run and chat gets a token of its own, so a token copied out of one run is not another run's, and the proxy knows whose launch a connection came from (the Network Log does not record it yet). Launches of one run share the run's token, and an installer's is its zone's, since installers are shared by every run. The proxy takes a launch's scope **only from a token it verified**, never from what the client claims, so an agent that sends `installer:agents` or another provider's id gets `407`, not that scope. Nothing is kept per launch, so a parked run or the chat container, reconnected after an ILD restart with the environment it was made with, still holds a token that verifies. Rotating the key (delete the file) invalidates every token at once.

A launch with no provider is judged by the global entries alone. A toolchain installer (ADR-0035) gets a token whose scope is its zone: its connections are judged by the global entries and the single **Installers** scope, and the log records the zone.

The key is held by the run host and the other launch sites that mint (ILD's services hold one instance of the credentials, passed in, not a process-wide global). What this is not: the token sits in the launch's own environment, so it is that launch's to use, and it lives as long as the key does; ending a run does not revoke it. A leaked token carries only its own launch's scope. Provider scope is still a convenience for keeping lists tidy per tool, not a boundary between agents on the same launch. Per-run or per-repository lists need a scope in the token other than the provider, and are a later layer. With runs as local processes (`dotnet run`, a developer's machine) the agent runs as ILD's user and can read the key file; that mode is unconfined anyway (ADR-0036).

### Where the proxy listens

- **Local runs:** loopback, which only the machine's own processes reach.
- **Container runs:** ILD has a different address on every run's network, so no single narrow bind serves them all and dynamic rebinding as networks come and go is not worth its moving parts. The proxy binds every interface, but answers only loopback and the subnet pool the run networks are cut from (`ILD_CONTAINER_SUBNET_POOL`), and drops any other peer before reading a byte. Compose peers and the LAN are outside the pool.
- **Kubernetes:** a pod reaches ILD at ILD's pod address, which a narrower bind would equal anyway, so the proxy binds every interface. The token, and the optional ingress NetworkPolicy, do the narrowing; the token is what stops another pod in the cluster.

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

- **`off` is unprotected by design.** No proxy means no log, and the DNS
  block does not apply either. Residual: processes launched
  in `off` keep direct egress until they exit, and other agent-uid processes can
  reach them through loopback or shared files (see ADR-0014), so a later
  `whitelist` agent could drive an earlier `off` one. A preview started in `off`
  is such a process: a dev server that reloads code a `whitelist` agent edits runs
  it with direct egress, until that service is started again.
- **Only what honours a proxy works for the agent under enforcement.** git over
  SSH, raw TCP, QUIC, anything reading neither `HTTP_PROXY` nor `HTTPS_PROXY`
  fails with a dropped connection rather than an error message. The standard
  toolchain (git over HTTPS, curl, npm, pip, the agent CLIs) all honour it, and
  Node's built-in `fetch` does too through `NODE_USE_ENV_PROXY=1`.
- **The proxy is the hostname oracle.** A tool that connects to an IP literal
  through the proxy is logged and judged by that literal; the lists can name IP
  literals exactly for that case.
- **Loopback is open, not just the proxy port.** The MCP server the agent CLI
  spawns must reach ILD's API, and previews are served on loopback; both are the
  agent's own. The agent uid has no DNS at all, loopback resolvers
  included, so there is no residual channel: names are resolved only by the
  proxy and forwards, as the orchestrator. Accepted risk: an agent tool that
  looks up a name without connecting through the proxy fails; `localhost`
  still resolves through `/etc/hosts`.
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

## Why there is no private-address block

Refusing every destination that resolves to a private, loopback or link-local
address, with a setting to allow chosen hosts, was considered and rejected. It
overrides the lists' plain meaning: a whitelisted LAN host would be refused until
it was listed a second time, and the modes would no longer say what they do.

- **LAN destinations are judged like any other.** The mode and lists decide, and
  the Network Log records the decision. `NO_PROXY` stays loopback-only and the
  firewall drops non-loopback agent traffic, so a LAN connection cannot skip the
  proxy.
- **To keep agents off the LAN,** use `whitelist`. `monitor` and `blacklist` do not
  protect it: a blacklist entry blocks only the names and literals it lists, not other
  names that resolve to the same address. Link-local and cloud metadata addresses
  (`169.254.169.254`) are refused by default (ADR-0036), and so is every other run's
  network.
