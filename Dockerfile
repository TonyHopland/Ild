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

# Install base utilities and optional tools before copying source so Docker
# layer caching skips tool installs when only source code changes.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates gosu netcat-openbsd && \
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

# Google Chrome ships an amd64-only Linux package, so it is installed only on
# amd64 — on other architectures (release builds publish amd64 + arm64) the step
# is a no-op rather than failing on a nonexistent package. The .deb installs
# /opt/google/chrome/chrome, exactly where Puppeteer's default `--channel
# stable` resolution looks (used by chrome-devtools-mcp, see opencode.json), so
# no extra config is needed. We don't purge wget/ca-certificates afterwards
# because google-chrome-stable depends on both.
#
# --no-install-recommends matters here: Chrome's recommends pull in the full
# mesa DRI driver set and the Noto font families, which headless Chrome never
# uses and which cost more than Chrome itself. fonts-liberation is added back
# explicitly so pages still render with sane default fonts.
RUN if [ "$WITH_CHROME" = "1" ] && [ "$(dpkg --print-architecture)" = "amd64" ]; then \
  apt-get update && \
  apt-get install -y --no-install-recommends wget ca-certificates && \
  wget -q -O /tmp/google-chrome.deb https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb && \
  apt-get install -y --no-install-recommends /tmp/google-chrome.deb fonts-liberation && \
  rm -f /tmp/google-chrome.deb && \
  rm -rf /var/lib/apt/lists/*; \
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
RUN mkdir -p /data /worktrees && \
  chown ild:ild /app /data /worktrees

COPY entrypoint.sh /entrypoint.sh
RUN sed -i 's/\r$//' /entrypoint.sh && chmod +x /entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]
CMD ["dotnet", "ILD.Api.dll"]
