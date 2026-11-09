# ADR-0020: Raw-TCP egress is served by a declared, judged relay, not a hole in the firewall

> **Amended by [ADR-0036](./0036-runs-always-in-containers.md).** In a run's or chat's container or pod, a forward's loopback port is answered by a `socat` relay ILD starts there before each command, tunnelling through the egress proxy with `CONNECT`; the proxy judges and logs the connection as it does every other.

A Worktree Preview cannot reach a database, or anything else outside the
container. Previews run as the agent uid ([ADR-0016](./0016-preview-runs-as-the-agent.md)),
whose nftables rules accept loopback and DNS and drop everything else
([ADR-0019](./0019-agent-egress-through-in-container-proxy.md)); the one permitted
hole is occupied by the egress proxy, which speaks HTTP `CONNECT`. Npgsql's first
bytes on the wire are a Postgres startup packet — it has no way to name its
destination in band and no proxy support — so the SYN is dropped in the kernel and
the connection times out with nothing recorded anywhere. The same is true of
Redis, Mongo, SMTP, JDBC, Node's built-in `fetch` and Vite's `server.proxy`: the
real dividing line is not "databases" but "does this client honour `HTTP_PROXY`".

We add **forwards**: operator-declared rows — a name, a destination host and port,
a loopback port — that the orchestrator serves as plain TCP relays. A preview
points its configuration at `127.0.0.1:<localPort>`; ILD accepts the connection,
judges it against the existing lists, records it in the existing log, and — as the
orchestrator uid, which the firewall rules never touch — dials the real
destination and relays bytes.

## Why not widen the firewall

Punching a uid-keyed hole for `postgres:5432` would work and record nothing. The
attempt would be invisible to the Network Log, unaffected by the whitelist, and
expressed in the one vocabulary ADR-0019 rejected — a rule that carries no host
name and needs a reload per edit. A forward keeps every property that ADR chose:
the destination is a **host name**, re-resolved per connection, so a rotated
address needs no edit; the row is database state, so adding, editing or deleting
one takes effect on the next connection with no restart; and it needs no
capability, no kernel NAT and no sysctl, so it behaves identically under compose,
podman and k3s, in advisory mode, and in single-uid mode.

## The forward is transport; the policy is the decision

A forward never implies an allow. Every accepted connection resolves the
destination, asks the same `IEgressPolicy` the proxy asks, and records the answer
through the same `INetworkLogRecorder` under the destination's **hostname**. A
blocked destination is closed at once rather than left to hang — being refused
promptly is the whole reason for answering the connection instead of letting the
kernel drop it. An open relay is re-judged and reset when its host is newly
blacklisted, through the same `EgressRelay` the proxy's tunnels use. The lists
remain the only thing that decides, so there is still one place to look when
something is blocked; the UI offers the whitelist entry in the same click, but it
is a list edit like any other.

## Why transparent interception is deferred

Uid-keyed DNAT into the same forwarder would remove the connection-string edit
entirely and make every dropped attempt visible. But by the time a packet exists
the hostname is gone, so the lists and the log would carry IP literals — which
ADR-0019 refused as a matching basis, and for the same reasons: CDNs, rotating
addresses, and rules that must be reloaded per edit. It layers cleanly on this
work later (same forwarder, same policy call, same log row) and depends on first
confirming `route_localnet` behaviour in the target pod.

## Consequences

- **`SSL Mode=Verify-Full` fails on `127.0.0.1:<localPort>`.** The client then
  addresses the server as `127.0.0.1`, so hostname verification cannot succeed.
  See the amendment below for the form that does work.
- **A loopback forward is reachable by any agent-uid process**, not only the
  preview that motivated it. Forwards are therefore instance-wide rather than
  per-repository: per-repository scoping would be attribution, not isolation. This
  is the existing shared-uid residual named in
  [ADR-0014](./0014-agent-uid-isolation.md) and closed by the same per-run-uid
  follow-up, not a new one.
- **Non-forwarded egress still fails silently.** A preview that tries to reach the
  outside without a forward is dropped by the kernel and ILD never sees the
  attempt. The Preview tab points at Settings → Network so the next person knows
  where to look; making those attempts visible is what the deferred interception
  above would buy.
- **The orchestrator dials the destination.** As with the proxy, the lists are the
  only thing standing between a forward and any host the orchestrator can reach.

## Amendment: forwards answer on their real name and port

In a run's or chat's container, each forward is also answered on the destination's
**real host name and port**, so a client that connects to `postgres:5432` reaches
the forward and a TLS client checks the certificate against `postgres`:
`SSL Mode=Verify-Full` (Npgsql), `sslmode=verify-full` (libpq) and `rediss://`
with host checking now work. This is possible because every run and chat has a
loopback and a hosts file of its own, which was not true when this ADR was written.

- Each forward host gets its own loopback address in `127.1.0.0/16`, from a hash of
  the name, so it is the same on every sync; forwards to one host on several ports
  share it. A hosts file maps a name to one address, which is why the address belongs
  to the name and not to the forward.
- ILD writes a marked block into the environment's `/etc/hosts` whenever it syncs
  the relays (before each command or terminal), and starts a relay on
  `<address>:<destination port>`. The tunnel is unchanged: `CONNECT host:port`
  through the egress proxy, judged and logged by host name, with TLS end to end.
- `127.0.0.1:<localPort>` keeps working, so existing connection strings do too; the
  real-name form is preferred.
- A destination that is an IP address has no name to map and keeps the loopback
  form only.
- The hosts file is root's in a Docker or Podman container, and ILD writes it as root
  without any capability, as it does for `resolv.conf`. In a Kubernetes pod kubelet
  owns it and commands run as the non-root user, so it is not writable: the relays
  keep the loopback form only there, and ILD logs that once. Hardening is not
  weakened to change this, and `hostAliases` cannot follow forwards that change at
  runtime. Under `dotnet run` there is one shared loopback, so only the loopback
  form exists.
- Names come from `/etc/hosts`, which the resolver reads before DNS, so this works
  with DNS turned off in a run.
