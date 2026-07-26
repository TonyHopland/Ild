ARG NODE_VERSION=24-alpine
ARG DOTNET_VERSION=10.0
# Selects the final stage's base image (see the final-base-* stages below).
# Declared here, before the first FROM, so it can be used in a FROM line.
ARG WITH_DOTNET_SDK=0

FROM node:${NODE_VERSION} AS frontend-build
WORKDIR /app

# Install git (required by vp config prepare script)
RUN apk add --no-cache git || (apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*)

# Copy workspace config and lockfile
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY frontend/package.json frontend/

# Install pnpm. Prefer corepack if available (Node <=24), otherwise install
# pnpm directly via npm (Node 25+ unbundled corepack).
RUN if command -v corepack >/dev/null 2>&1; then \
      corepack enable && corepack prepare pnpm@latest --activate; \
    else \
      npm install -g pnpm@latest; \
    fi && \
    pnpm install --frozen-lockfile

# Copy frontend source and build
COPY frontend/ frontend/
WORKDIR /app/frontend
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Optional informational version override stamped by CI (see
# docs/adr/0012-ghcr-image-tagging-strategy.md). Empty by default so local and
# compose builds keep the version from Directory.Build.props.
ARG VERSION=

COPY ILD.sln ./
COPY ILD.Data/ILD.Data.csproj ILD.Data/
COPY ILD.Core/ILD.Core.csproj ILD.Core/
COPY ILD.Api/ILD.Api.csproj ILD.Api/
COPY ILD.Tests/ILD.Tests.csproj ILD.Tests/
COPY ILD.McpServer/ILD.McpServer.csproj ILD.McpServer/
COPY ILD.WorkItemServer/ILD.WorkItemServer.csproj ILD.WorkItemServer/
RUN dotnet restore
COPY . .
RUN mkdir -p /certs && \
  if [ -d /src/certs ]; then cp -a /src/certs/. /certs/; fi
WORKDIR /src/ILD.Api
RUN dotnet publish -c Release -o /app/publish --no-restore ${VERSION:+-p:Version=$VERSION}

# Build the MCP server so it can be shipped alongside ILD.Api
WORKDIR /src/ILD.McpServer
RUN dotnet publish -c Release -o /app/mcp-server --no-restore ${VERSION:+-p:Version=$VERSION}

# The final stage's base is picked by WITH_DOTNET_SDK: work-item execution that
# builds .NET repos needs the SDK, so in that case we start from the official
# SDK image rather than layering a second, separately-downloaded SDK on top of
# the ASP.NET runtime image (which would ship two copies of the shared
# frameworks). Both bases are Debian-derived, so the apt steps below are
# identical either way.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final-base-0
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS final-base-1

FROM final-base-${WITH_DOTNET_SDK} AS final
WORKDIR /app

ARG WITH_NODE=0
ARG NODE_RUNTIME_VERSION=24.15.0
ARG WITH_CHROME=0
ARG WITH_CERTS=0
ARG APP_UID=10001
ARG APP_GID=10001
# Second, lower-trust user the coding-agent CLI runs as, plus the group both it
# and the orchestrator share for the worktree + /data agent installs (ADR-0014).
ARG AGENT_UID=10002
ARG AGENT_GID=10002
ARG SHARED_GID=10003

# Install base utilities and optional tools before copying source so Docker
# layer caching skips tool installs when only source code changes.
# libcap2-bin (capsh) + util-linux (setpriv): the entrypoint keeps ambient
# CAP_SETUID/SETGID on the orchestrator so it can drop the agent CLI to a second
# uid; acl (setfacl): default ACLs make shared dirs read/write across both uids.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates gosu netcat-openbsd libcap2-bin util-linux acl && \
    mkdir -p /usr/local/share/ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Create the runtime user up front. ILD runs as this non-root user (the
# entrypoint drops to it via gosu) and owns /app, /data and /worktrees;
# the managed coding-agent installs land under /data owned by ild. Reuses
# any existing user/group at the configured UID/GID.
RUN existing_group="$(getent group "${APP_GID}" | cut -d: -f1 || true)" && \
    if [ -n "$existing_group" ] && [ "$existing_group" != "ild" ]; then \
      groupmod -n ild "$existing_group"; \
    elif [ -z "$existing_group" ]; then \
      groupadd --gid ${APP_GID} ild; \
    fi && \
    existing_user="$(getent passwd "${APP_UID}" | cut -d: -f1 || true)" && \
    if [ -n "$existing_user" ] && [ "$existing_user" != "ild" ]; then \
      usermod -l ild -g ild -d /home/ild -m -s /usr/sbin/nologin "$existing_user"; \
    elif [ -z "$existing_user" ]; then \
      useradd --uid ${APP_UID} --gid ${APP_GID} --create-home --home-dir /home/ild --shell /usr/sbin/nologin ild; \
    fi

# Create the lower-trust agent user and the shared group (ADR-0014). The coding
# agent CLI runs as `agent` so it no longer shares the orchestrator's uid; the
# `ild-agents` group is what grants both users access to the worktree tree and
# the /data agent installs, while /data's secrets stay ild-only. Both users are
# members of the shared group; the runtime paths' ownership/modes are applied by
# the entrypoint (volumes overlay any build-time perms). The entrypoint sets both
# homes to 0710/0750 group ild-agents at runtime — the agent traverses /home/ild
# to resolve the credential-store and .gitconfig symlinks beneath it via the
# GROUP, not via world bits; this chmod is only a sane build-time baseline for the
# non-isolated case (useradd's Debian HOME_MODE is 0750 group ild, which the agent
# is not in).
RUN if [ -z "$(getent group "${SHARED_GID}" | cut -d: -f1 || true)" ]; then \
      groupadd --gid ${SHARED_GID} ild-agents; \
    fi && \
    if [ -z "$(getent group "${AGENT_GID}" | cut -d: -f1 || true)" ]; then \
      groupadd --gid ${AGENT_GID} agent; \
    fi && \
    if [ -z "$(getent passwd "${AGENT_UID}" | cut -d: -f1 || true)" ]; then \
      useradd --uid ${AGENT_UID} --gid ${AGENT_GID} --create-home --home-dir /home/agent --shell /usr/sbin/nologin agent; \
    fi && \
    usermod -aG ild-agents ild && \
    usermod -aG ild-agents agent && \
    chmod 0755 /home/agent /home/ild

# Let the agent uid run git in trees the orchestrator owns. `git worktree add`
# runs as ild, so the worktree, its .git file and the gitdir under
# /data/repos/<repo>/.git/worktrees/<name> are all owned by uid 10001 — and since
# 2.35.2 git compares the repository owner's uid to geteuid() and refuses with
# "detected dubious ownership" otherwise. Group membership and mode 2775 do not
# enter into that check, so without this every git command the agent runs (the
# review prompts use git log/diff/status, and it commits its own work) fails.
#
# This must be system-level: git ignores safe.directory from repository config,
# and /home/agent/.gitconfig is a symlink onto the read-only host mount so it
# cannot carry it either. `*` rather than per-path entries because the trailing
# `/*` form is version-dependent and would fail silently on an older git. It
# costs nothing here: safe.directory guards against picking up a repo owned by
# some *other* user, whereas both uids are ours and the sharing is deliberate —
# the real boundary is the uid/group/mode scheme, not this heuristic.
RUN git config --system --add safe.directory '*'

# Coding agents (Pi, OpenCode, Claude Code) are intentionally NOT baked into
# the image. They are installed on demand onto the persistent /data volume
# from the AI Provider page (npm-based), so they can be updated without
# rebuilding the image and survive redeploys. Node/npm below (WITH_NODE) is
# what those runtime installs and version checks use.
RUN if [ "$WITH_NODE" = "1" ]; then \
  apt-get update && \
  apt-get install -y --no-install-recommends curl xz-utils && \
  if [ "$NODE_RUNTIME_VERSION" = "latest" ]; then \
    NODE_RUNTIME_VERSION=$(curl -fsSL https://nodejs.org/dist/index.json | sed -n 's/.*"version":"\(v[^"]*\)".*/\1/p' | head -n1 | sed 's/^v//'); \
  fi && \
  case "$(dpkg --print-architecture)" in \
    amd64) NODE_ARCH=x64 ;; \
    arm64) NODE_ARCH=arm64 ;; \
    armhf) NODE_ARCH=armv7l ;; \
    *) echo "Unsupported architecture: $(dpkg --print-architecture)" >&2; exit 1 ;; \
  esac && \
  curl -fsSL "https://nodejs.org/dist/v${NODE_RUNTIME_VERSION}/node-v${NODE_RUNTIME_VERSION}-linux-${NODE_ARCH}.tar.xz" -o node.tar.xz && \
  tar -xf node.tar.xz -C /usr/local --strip-components=1 && \
  rm node.tar.xz && \
  if command -v corepack >/dev/null 2>&1; then corepack enable; else npm install -g pnpm@latest; fi && \
  apt-get purge -y curl xz-utils && \
  apt-get autoremove -y && \
  rm -rf /var/lib/apt/lists/*; \
fi

# Google publishes google-chrome-stable for both amd64 and arm64 Linux (arm64
# since 2025, https://blog.google/chromium/bringing-chrome-to-arm64-linux-devices/),
# which is exactly the pair of architectures release builds ship (ADR-0012), so
# WITH_CHROME=1 is honoured on both — the package name is the only difference.
# Anything else (armhf) has no upstream .deb: that skips with a message instead
# of failing, unlike the Node step above which exits 1, because Chrome is an
# opt-in extra and an image without a browser beats no image at all. Both .debs
# install /opt/google/chrome/chrome, exactly where Puppeteer's default
# `--channel stable` resolution looks (used by chrome-devtools-mcp, see
# opencode.json), so no extra config is needed on either architecture. We don't
# purge wget/ca-certificates afterwards because google-chrome-stable depends on
# both.
#
# --no-install-recommends matters here: Chrome's recommends pull in the full
# mesa DRI driver set and the Noto font families, which headless Chrome never
# uses and which cost more than Chrome itself. fonts-liberation is added back
# explicitly so pages still render with sane default fonts.
RUN if [ "$WITH_CHROME" = "1" ]; then \
  CHROME_ARCH="$(dpkg --print-architecture)"; \
  case "$CHROME_ARCH" in \
    amd64|arm64) \
      apt-get update && \
      apt-get install -y --no-install-recommends wget ca-certificates && \
      wget -q -O /tmp/google-chrome.deb "https://dl.google.com/linux/direct/google-chrome-stable_current_${CHROME_ARCH}.deb" && \
      apt-get install -y --no-install-recommends /tmp/google-chrome.deb fonts-liberation && \
      rm -f /tmp/google-chrome.deb && \
      rm -rf /var/lib/apt/lists/* ;; \
    *) echo "Skipping Chrome: no upstream package for $CHROME_ARCH" >&2 ;; \
  esac; \
fi

COPY --from=build /certs /tmp/extra-certs
RUN if [ "$WITH_CERTS" = "1" ]; then \
      copied=0; \
      for cert in /tmp/extra-certs/*.crt /tmp/extra-certs/*.pem; do \
        [ -e "$cert" ] || continue; \
        cp "$cert" /usr/local/share/ca-certificates/; \
        copied=1; \
      done; \
      if [ "$copied" -eq 1 ]; then update-ca-certificates; fi; \
    fi && \
    rm -rf /tmp/extra-certs

# --chown on the COPY itself: a follow-up `chown -R /app` would rewrite every
# file into an extra layer, shipping the published output twice.
COPY --from=build --chown=ild:ild /app/publish ./
COPY --from=build --chown=ild:ild /app/mcp-server/ ./
COPY --from=frontend-build --chown=ild:ild /app/frontend/dist ./wwwroot
ENV HOME=/home/ild
ENV ILD_DATA_PATH=/data
ENV ILD_WORKTREES_PATH=/worktrees

# uid-isolation wiring (ADR-0014). These drive the entrypoint's two-uid setup;
# they are unset in Dockerfile.WorkItemServer, so its entrypoint keeps the
# single-uid gosu drop. The app-side ILD_AGENT_* vars are deliberately NOT set
# here: the entrypoint exports them from AGENT_USER/GROUP/HOME so there is one
# source of truth. Setting them here independently would mean that clearing
# AGENT_USER (the documented single-uid escape hatch) left the app still routing
# every launch through setpriv, failing every run with an opaque EPERM.
ENV AGENT_USER=agent
ENV AGENT_GROUP=agent
ENV AGENT_HOME=/home/agent
ENV SHARED_GROUP=ild-agents
# cap_kill is required as well as cap_setuid/cap_setgid: setpriv gives the agent
# a different real AND saved uid, so kill(2) from the orchestrator returns EPERM
# without it — Halt and the per-node timeouts would leave the agent orphaned in
# the worktree. The agent itself still gets no capabilities (setpriv clears the
# inheritable + ambient sets, so its post-exec permitted set is empty).
ENV RUNTIME_AMBIENT_CAPS=cap_setuid,cap_setgid,cap_kill
# Shared read/write: the agent writes its worktree, and git worktree commits go
# through the base repo's object store under /data/repos.
# Scratch both uids touch (per-run agent session state, the interactive
# terminal cwd). It lives under /tmp so it is discarded with the container
# rather than growing on a volume, but it is set up like the other shared trees
# so that a file the orchestrator seeds there stays writable by the agent.
ENV AGENT_SCRATCH_DIR=/tmp/ild-agent-scratch
# Orchestrator-only state (the git askpass helper, preview state). Created
# owner-only by the entrypoint before anything else runs, which is what stops the
# agent planting a file the orchestrator would then execute as itself. On /tmp so
# it stays ephemeral instead of growing on the data volume.
ENV ORCHESTRATOR_PRIVATE_DIR=/tmp/ild-orchestrator-private
ENV SHARED_RW_DIRS="/worktrees /home/ild/.agent-config /data/repos /data/chat-sessions /tmp/ild-agent-scratch"
# Shared read-only: the agent execs the npm-installed CLIs but must not be able
# to rewrite them — the orchestrator runs those same binaries as ild (version
# checks, the provider terminal), so a writable install would be a way back
# across the boundary. The orchestrator still installs/updates them as the owner.
ENV SHARED_RO_DIRS=/data/agents
ENV DATA_TRAVERSE_DIRS=/data

# Baseline ownership; the entrypoint finalizes modes + default ACLs on the
# volume-mounted paths at startup (a named volume overlays these build-time
# perms). /worktrees and the agent-config store are group-owned by the shared
# group up front; /data stays ild-private and is opened to traverse-only later.
RUN mkdir -p /data /worktrees /home/ild/.agent-config && \
  chown ild:ild /app /data && \
  chown ild:ild-agents /worktrees /home/ild/.agent-config

COPY entrypoint.sh /entrypoint.sh
RUN sed -i 's/\r$//' /entrypoint.sh && chmod +x /entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]
CMD ["dotnet", "ILD.Api.dll"]
