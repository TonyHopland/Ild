# Worktree previews are routed by wildcard subdomain, not by path prefix

A Worktree Preview binds each of its services to a port allocated at runtime
inside the ILD container. Once ILD runs behind an ingress — or simply inside a
container that publishes one port — nothing outside can reach those ports, so the
`http://<publicHost>:<port>` URL the Preview tab has always shown resolves to
nothing. We route previews through ILD's own port instead, on wildcard
subdomains of `ILD_PREVIEW_PROXY_BASE`: `wi-<workItemId>.<base>` reaches the
profile's `"public": true` service and `wi-<workItemId>-<serviceName>.<base>`
reaches one service by name.

## Considered options

**Path prefixes** (`/_preview/wi-12/...`) were the obvious alternative and need no
DNS or certificate work. They were rejected because a preview is somebody else's
application: it emits absolute paths in HTML, CSS `url()`, source maps, service
worker scopes, cookies and WebSocket URLs, none of which know about a prefix.
Serving one correctly under a prefix means rewriting response _bodies_ — an
open-ended job that is wrong for every framework in a different way, and one the
previewed app can defeat at any time by constructing a URL in JavaScript. A
subdomain gives each preview its own origin, so every absolute path is already
right, and cookie and storage isolation between previews comes for free rather
than being a thing we would have to enforce.

The cost is real and is the reason this is written down: wildcard DNS for
`*.<base>` and a wildcard SAN on the ingress certificate have to exist before
anything works, and neither can be provisioned from this repository.

## Consequences

- `ILD_PREVIEW_PROXY_BASE` is both the configuration and the feature's opt-in
  gate. Unset — the default — no request is proxied and preview URLs keep their
  historical direct form.
- **A running proxied preview is unauthenticated.** The proxy must run ahead of
  `UseAuthentication()`/`UseAuthorization()`, since a foreign app cannot carry an
  ILD session token. That is the price of the feature, not an oversight; see
  `docs/deployment.md`.
- Everything the proxy will not forward answers with one indistinguishable 404,
  and the reason goes to the log. A page per failure mode would have told an
  unauthenticated caller which work items exist, which have worktrees, and what
  is running in them — free to enumerate, since the hostname it keys off is a
  client-supplied `Host` header. The wildcard DNS above is what makes previews
  usable in a browser; it is not an access control.
- The work item id in a hostname is parsed as digits immediately after `wi-`.
  That is what makes `wi-12-work-item-server` unambiguous, and it means preview
  hostnames only work for numeric work item ids.
- `Host` is rewritten to the loopback target by default, because host-checking
  dev servers reject the preview hostname outright. Services that would rather
  see the real hostname opt out with `"rewriteHost": false`.
- A dev server's hot-reload channel generally will not reconnect through the
  proxy. Pages load; live reload is a per-framework configuration problem we
  document rather than solve.
