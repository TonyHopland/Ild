# ADR-0012: GHCR image tagging strategy — `latest` is the newest release, not `main`

Both deployable images (`ild` and `ild-workitem-server`) are published to GHCR from CI. A push to `main` publishes only the moving `main` tag (amd64); a `vX.Y.Z` git tag publishes `X.Y.Z`, `X.Y`, and `latest` (amd64 + arm64). We deliberately point `latest` at the newest *release* rather than at `main`, so a bare `docker pull` yields a stable released version — matching the README's "clone and run" expectation — and we do not publish immutable `main-<sha>` tags. Release images instead embed provenance via a build-time `dotnet publish -p:Version=X.Y.Z` override; `main` images stamp `<base>-main+<shortsha>` so they remain traceable to a commit.

## Consequences

- Consumers pin a stable line with `latest`/`X.Y`/`X.Y.Z`; tracking the bleeding edge is an explicit opt-in to the `main` tag.
- The moving `main` tag is not rollback-pinnable by design; the embedded `-main+<shortsha>` version is the only way to identify which commit a `main` image came from.
- arm64 is built (release tags only) under QEMU emulation, so release publishes are slow but infrequent; `main` builds stay amd64-only and fast.
- `latest` is a contract once consumers pull it — repointing it at `main` later would be a breaking change.
