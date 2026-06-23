# ADR-0012: GHCR image tagging strategy — `latest` is the newest release, not `main`

Both deployable images (`ghcr.io/tonyhopland/ild` and `ghcr.io/tonyhopland/ild-workitem-server`) are built and pushed to GHCR from CI by a build-gated `publish` job. A push to `main` publishes only the moving `main` tag (amd64); a `vX.Y.Z` git tag publishes `X.Y.Z`, `X.Y`, and `latest` (amd64 + arm64). We deliberately point `latest` at the newest _release_ rather than at `main`, so a bare `docker pull` yields a stable released version — matching the README's "clone and run" expectation — and we do not publish immutable `main-<sha>` tags. This records the tagging, architecture, and version-stamping rules, which embed a few deliberate trade-offs.

**Scope is publish-only.** CI pushes images, but the deploy path is unchanged — `docker-compose.yml` stays on `--build`. Wiring compose/the Pi to _pull_ these images is a separate follow-up. Until then the published images exist but nothing consumes them, which is intentional: it lets the pipeline be validated (and packages flipped public) before anything depends on them.

**The canonical release tag is `vX.Y.Z`; the stray bare `0.2.0` git tag is ignored.** The CI trigger filters tags to `v*`, and `compute-version.sh` rejects any tag that is not `vX.Y.Z`, so the legacy bare tag can never trigger a publish. The image tag strips the leading `v`.

**arm64 is release-only, built via QEMU emulation.** Emulated arm64 builds are slow, but releases are infrequent, so the cost is acceptable; `main` builds stay amd64-only to keep the common path fast.

**Version stamping overrides only the informational `Version`.** Releases publish with `dotnet publish -p:Version=X.Y.Z` (from the tag); `main` builds stamp `<props-base>-main+<shortsha>` (base from `Directory.Build.props`). In both cases the numeric `AssemblyVersion`/`FileVersion` keep the props values, so assembly identity stays clean and only the human-facing version carries the build provenance. The override flows in as the `VERSION` Docker build arg, which is empty for local/compose builds so they keep the props version unchanged.

**The informational version is the in-image version.** Both images report it on `/health` (ILD's `HealthController`, the WorkItem Server's `/health`), reading `AssemblyInformationalVersionAttribute` rather than the pinned numeric `AssemblyVersion`. So a release stamps `Version=X.Y.Z` and `/health` reports `X.Y.Z` == the tag. `IncludeSourceRevisionInInformationalVersion` is set `false` in `Directory.Build.props` so the SDK does not append a `+<git-sha>` to the informational version, which would otherwise make a release surface `X.Y.Z+<sha>` instead of the bare tag.

## Consequences

- **Consumers pin a stable line with `latest`/`X.Y`/`X.Y.Z`;** tracking the bleeding edge is an explicit opt-in to the `main` tag. `latest` is a contract once consumers pull it — repointing it at `main` later would be a breaking change.
- **The moving `main` tag is not rollback-pinnable by design;** the embedded `<base>-main+<shortsha>` version is the only way to identify which commit a `main` image came from.
- **One manual, one-time step per package.** The first push lands each GHCR package **private**; each must be flipped to **public** once in package settings (documented in [deployment](../deployment.md#published-images)). This cannot be automated from the push.
- **The publish never runs on PRs or on a red build.** `publish` is a reusable workflow (`on: workflow_call`) invoked from `ci.yml` with `needs: build-and-test` and `if: github.event_name != 'pull_request'`, authenticating with `GITHUB_TOKEN` (`packages: write`).
- **Version logic is unit-tested.** `compute-version.sh` is covered by `compute-version.test.sh`, run as a CI step so the stamping rules are verified before any publish can use them.
