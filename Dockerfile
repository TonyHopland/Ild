ARG NODE_VERSION=26-alpine
ARG DOTNET_VERSION=10.0
# The base of the slim runner image (the `runner` target).
ARG RUNNER_BASE=ubuntu:24.04

FROM node:${NODE_VERSION} AS frontend-build
WORKDIR /app

# Install git (required by vp config prepare script)
RUN apk add --no-cache git || (apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*)

# Copy workspace config and lockfile
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY frontend/package.json frontend/

# Install the pnpm that package.json's packageManager pins, so the lockfile is
# read by the version that wrote it. Corepack (Node <=24) reads the pin itself
# when given no version; Node 25+ no longer ships corepack, so there the pin is
# read from package.json (minus any +sha suffix) and installed with npm. No pin
# fails the build rather than guessing a version.
RUN if command -v corepack >/dev/null 2>&1; then \
      corepack enable && corepack prepare --activate; \
    else \
      pnpm_version="$(node -p "(/^pnpm@([^+]+)/.exec(require('./package.json').packageManager || '') || [])[1] || ''")" && \
      if [ -z "$pnpm_version" ]; then \
        echo "package.json has no pnpm packageManager pin to install" >&2; \
        exit 1; \
      fi && \
      npm install -g "pnpm@$pnpm_version"; \
    fi && \
    pnpm --version && \
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

# The same server for the slim runner image, self-contained: a run has no .NET of ILD's, and the one its project
# brings may be too old to run the server, or absent. Started through its apphost, /app/ild-mcp-server, the path
# the agent's MCP config names in both images.
ARG TARGETARCH
RUN rid="linux-$( [ "${TARGETARCH:-amd64}" = "arm64" ] && echo arm64 || echo x64 )" && \
    dotnet publish -c Release -r "$rid" --self-contained -p:PublishSingleFile=true \
      -o /app/mcp-server-runner ${VERSION:+-p:Version=$VERSION}

# ILD itself. Runs, chats and sign-ins never execute here: they get containers of their own from the `runner` image
# below (pods in Kubernetes), so this image holds no language toolchain, browser or second user. It keeps git, for
# ILD's mirrors and base clones, and Node with npm, which install the coding agents onto /data and check their versions.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app

ARG NODE_RUNTIME_VERSION=26.13.0
ARG NPM_VERSION=12.2.1
ARG WITH_CERTS=0
# Not APP_UID/APP_GID: the .NET base images set those as ENV (1654), and an ENV wins over an ARG of the same
# name, which silently made ild uid 1654.
ARG ILD_UID=10001
ARG ILD_GID=10001

# gosu: the entrypoint drops from root to ild. netcat: it waits for PostgreSQL.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates gosu netcat-openbsd && \
    mkdir -p /usr/local/share/ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Create the runtime user up front. ILD runs as this non-root user (the
# entrypoint drops to it via gosu) and owns /app, /data and /worktrees;
# the managed coding-agent installs land under /data owned by ild. Reuses
# any existing user/group at the configured UID/GID. A rootful engine's socket
# is not given this group but another gid (ILD_CONTAINER_SOCKET_GRANT, ILD_CONTAINER_SOCKET_GID).
RUN existing_group="$(getent group "${ILD_GID}" | cut -d: -f1 || true)" && \
    if [ -n "$existing_group" ] && [ "$existing_group" != "ild" ]; then \
      groupmod -n ild "$existing_group"; \
    elif [ -z "$existing_group" ]; then \
      groupadd --gid ${ILD_GID} ild; \
    fi && \
    existing_user="$(getent passwd "${ILD_UID}" | cut -d: -f1 || true)" && \
    if [ -n "$existing_user" ] && [ "$existing_user" != "ild" ]; then \
      usermod -l ild -g ild -d /home/ild -m -s /usr/sbin/nologin "$existing_user"; \
    elif [ -z "$existing_user" ]; then \
      useradd --uid ${ILD_UID} --gid ${ILD_GID} --create-home --home-dir /home/ild --shell /usr/sbin/nologin ild; \
    fi

# Coding agents (Pi, OpenCode, Claude Code) are intentionally NOT baked into
# the image. They are installed on demand onto the persistent /data volume
# from the AI Provider page (npm-based), so they can be updated without
# rebuilding the image and survive redeploys; a run gets the same versions in
# its container (docs/adr/0035-run-toolchains.md).
#
# NPM_VERSION replaces the npm that ships with Node. Empty keeps Node's bundled
# npm. The smoke check runs it as ild with a throwaway HOME and fails the build
# unless it reports NPM_VERSION. libatomic1 is a runtime dependency of the
# official Node 26 binaries; it is installed explicitly so the autoremove below
# keeps it.
RUN apt-get update && \
  apt-get install -y --no-install-recommends curl xz-utils libatomic1 && \
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
  if [ -n "$NPM_VERSION" ]; then \
    npm install -g --prefix /usr/local --cache /tmp/npm-install-cache --no-audit --no-fund "npm@$NPM_VERSION" && \
    rm -rf /tmp/npm-install-cache /root/.npm && \
    smoke_home="$(mktemp -d)" && \
    chown "${ILD_UID}:${ILD_GID}" "$smoke_home" && \
    actual="$(gosu "${ILD_UID}:${ILD_GID}" env HOME="$smoke_home" sh -lc 'npm --version')"; \
    rm -rf "$smoke_home"; \
    if [ "$actual" != "$NPM_VERSION" ]; then \
      echo "npm reports '$actual', but NPM_VERSION=$NPM_VERSION" >&2; \
      exit 1; \
    fi; \
  fi && \
  apt-get purge -y curl xz-utils && \
  apt-get autoremove -y && \
  rm -rf /var/lib/apt/lists/*

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
# ILD runs from this image: runs, chats and sign-ins go to containers, or to pods in Kubernetes
# (ILD.Core/Services/RunHosts/RunHostOptions.cs). Not a setting; nothing else sets it.
ENV ILD_IMAGE=1
# Port of the agent egress proxy (ADR-0019), which each run's and chat's container is pointed at.
ENV ILD_NETWORK_PROXY_PORT=3128

# The agent-config store holds the provider logins and is ILD's alone
# (docs/adr/0031-model-gateway.md).
RUN mkdir -p /data /worktrees /home/ild/.agent-config && \
  chown ild:ild /app /data /worktrees /home/ild/.agent-config && \
  chmod 0700 /home/ild/.agent-config

COPY entrypoint.sh /entrypoint.sh
RUN sed -i 's/\r$//' /entrypoint.sh && chmod +x /entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]
CMD ["dotnet", "ILD.Api.dll"]

# The slim image a loop run's pod or container runs under (docs/adr/0035-run-toolchains.md): an OS, the tools every
# project uses (git, a shell, a compiler, curl, jq, ripgrep), socat for the declared forwards, Chrome and mise. No language toolchain and no coding agent: ILD
# installs the ones a repository needs, at the versions it picks, into a shared store the run mounts read-only, so a
# node project does not carry a .NET SDK. The same image installs them: ILD runs mise in it against the store.
# The MCP server is the self-contained build, at the path the agent's MCP config names. tini is the init, which reaps
# what a killed command leaves behind. /ild-run and /ild-chat are the runner's, so the named volume a container run host mounts there
# starts out writable by it (a volume takes the ownership of the image directory).
FROM ${RUNNER_BASE} AS runner
ARG RUNNER_UID=10001
# Pinned like Node and npm, so a new mise release cannot change the image without a commit. mise's apt repository
# lists only its newest version, so apt cannot hold a pin: the .deb for the pinned version is downloaded from the
# repository's pool and checked against the SHA-256 below, one per architecture. To bump, change the version and both sums.
ARG MISE_VERSION=2026.11.4
ARG MISE_SHA256_AMD64=207ae03669918b4cc98ff505526f16568cbe0a4b5e6c9da97029600f6f3dbefd
ARG MISE_SHA256_ARM64=841f105cc4d415437630b07a7f81f0e3d3ace2298295e3bbd69ef2dd3b656177
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
      bash tini git ca-certificates curl wget gnupg xz-utils unzip zip jq ripgrep build-essential socat \
      libicu74 libatomic1 zlib1g procps less openssh-client && \
    arch="$(dpkg --print-architecture)" && \
    case "$arch" in amd64) mise_sha="$MISE_SHA256_AMD64" ;; arm64) mise_sha="$MISE_SHA256_ARM64" ;; *) echo "no mise package for $arch" >&2; exit 1 ;; esac && \
    wget -qO /tmp/mise.deb "https://mise.jdx.dev/deb/pool/main/mise_${MISE_VERSION}_${arch}.deb" && \
    echo "$mise_sha  /tmp/mise.deb" | sha256sum -c - && \
    apt-get install -y --no-install-recommends /tmp/mise.deb && rm /tmp/mise.deb && \
    rm -rf /var/lib/apt/lists/* && \
    # A forward's host name is answered from /etc/hosts, which a run with DNS off still has to read: fail the build if the resolver stops doing that.
    grep -Eq '^hosts:.*[[:space:]]files([[:space:]]|$)' /etc/nsswitch.conf && \
    (getent group ${RUNNER_UID} >/dev/null || groupadd --gid ${RUNNER_UID} runner) && \
    (getent passwd ${RUNNER_UID} >/dev/null || useradd --uid ${RUNNER_UID} --gid ${RUNNER_UID} --no-create-home --home-dir /tmp --shell /bin/bash runner) && \
    git config --system --add safe.directory '*' && \
    mkdir -p /app /ild-run /ild-chat /opt/ild-tools /opt/ild-toolchains /opt/ild-repo-tools && chown ${RUNNER_UID}:${RUNNER_UID} /ild-run /ild-chat /opt/ild-tools /opt/ild-toolchains /opt/ild-repo-tools && chmod 0755 /ild-run /ild-chat
# Chrome, for the chrome-devtools MCP server agents drive a browser with: it needs system libraries the store cannot
# provide, so it is part of the image, at /opt/google/chrome/chrome where Puppeteer's stable channel looks. Google publishes
# google-chrome-stable for amd64 and arm64 only. Its recommends are left out and fonts-liberation added back.
# RUNNER_WITH_CHROME=0 builds the image without it.
ARG RUNNER_WITH_CHROME=1
RUN if [ "$RUNNER_WITH_CHROME" = "1" ]; then \
      arch="$(dpkg --print-architecture)"; \
      case "$arch" in \
        amd64|arm64) \
          apt-get update && \
          wget -q -O /tmp/google-chrome.deb "https://dl.google.com/linux/direct/google-chrome-stable_current_${arch}.deb" && \
          apt-get install -y --no-install-recommends /tmp/google-chrome.deb fonts-liberation && \
          rm -f /tmp/google-chrome.deb && rm -rf /var/lib/apt/lists/* ;; \
        *) echo "Skipping Chrome: no upstream package for $arch" >&2 ;; \
      esac; \
    fi
COPY --from=build --chown=${RUNNER_UID}:${RUNNER_UID} /app/mcp-server-runner/ /app/
ENV HOME=/tmp
USER ${RUNNER_UID}
WORKDIR /app
ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["sleep", "infinity"]

# The default target: what `docker build .` makes.
FROM final AS ild
