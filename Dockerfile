ARG NODE_VERSION=24-alpine
ARG DOTNET_VERSION=10.0

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

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app

ARG WITH_OPENCODE=0
ARG WITH_PI=0
ARG WITH_CLAUDE_CODE=0
ARG WITH_NODE=0
ARG NODE_RUNTIME_VERSION=24.15.0
ARG WITH_DOTNET_SDK=0
ARG DOTNET_SDK_CHANNEL=10.0
ARG WITH_CHROME=0
ARG WITH_CERTS=0
ARG APP_UID=10001
ARG APP_GID=10001

# Install base utilities and optional tools before copying source so Docker
# layer caching skips tool installs when only source code changes.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates gosu netcat-openbsd && \
    mkdir -p /usr/local/share/ca-certificates && \
    rm -rf /var/lib/apt/lists/*

# Create the runtime user up front so subsequent install steps that need a
# real (non-root) HOME — e.g. the Claude Code launcher, which uses absolute
# symlinks into $HOME/.local/share — can install into /home/ild directly
# via gosu. Reuses any existing user/group at the configured UID/GID.
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

# The opencode install script drops files under $HOME/.opencode. Because ILD
# runs as a non-root runtime user, keep that install outside /root so the
# binary and any adjacent assets remain executable after gosu switches users.
RUN if [ "$WITH_OPENCODE" = "1" ]; then \
      apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && \
      curl -fsSL https://opencode.ai/install | bash && \
  mkdir -p /opt && \
  rm -rf /opt/opencode && \
  cp -a /root/.opencode /opt/opencode && \
  chmod -R a+rX /opt/opencode && \
  ln -sf /opt/opencode/bin/opencode /usr/local/bin/opencode && \
      rm -rf /var/lib/apt/lists/*; \
    fi

RUN if [ "$WITH_NODE" = "1" ]; then \
  apt-get update && \
  apt-get install -y ca-certificates curl xz-utils && \
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
  apt-get remove -y ca-certificates curl && \
  apt-get autoremove -y && \
  rm -rf /var/lib/apt/lists/*; \
fi

RUN if [ "$WITH_PI" = "1" ]; then \
  if ! command -v npm >/dev/null 2>&1; then \
    echo "WITH_PI=1 requires Node/npm. Enable WITH_NODE=1 as well." >&2; \
    exit 1; \
  fi && \
  npm install -g @earendil-works/pi-coding-agent; \
fi

# Claude Code ships a native binary via https://claude.ai/install.sh. The
# launcher at $HOME/.local/bin/claude is an absolute symlink into
# $HOME/.local/share/claude/versions/<ver>, so the install tree must stay
# where it lands. We run the installer as the ild user (gosu sets
# HOME=/home/ild) so files are owned by the runtime user from the start
# and the launcher's symlinks stay valid; a /usr/local/bin/claude symlink
# exposes the binary on PATH for any user inside the container.
# Auth is handled at runtime via `claude /login` (Max subscription) or
# CLAUDE_CODE_OAUTH_TOKEN.
RUN if [ "$WITH_CLAUDE_CODE" = "1" ]; then \
      apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && \
      gosu ild sh -c 'curl -fsSL https://claude.ai/install.sh | bash' && \
      ln -sf /home/ild/.local/bin/claude /usr/local/bin/claude && \
      rm -rf /var/lib/apt/lists/*; \
    fi

RUN if [ "$WITH_DOTNET_SDK" = "1" ]; then \
  apt-get update && \
  apt-get install -y ca-certificates curl && \
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh && \
  chmod +x dotnet-install.sh && \
  if [ "$DOTNET_SDK_CHANNEL" = "latest" ]; then \
    ./dotnet-install.sh --channel STS --install-dir /usr/share/dotnet; \
  else \
    ./dotnet-install.sh --channel "$DOTNET_SDK_CHANNEL" --install-dir /usr/share/dotnet; \
  fi && \
  rm dotnet-install.sh && \
  apt-get remove -y ca-certificates curl && \
  apt-get autoremove -y && \
  rm -rf /var/lib/apt/lists/*; \
fi

RUN if [ "$WITH_CHROME" = "1" ]; then \
  apt-get update && \
  apt-get install -y ca-certificates wget gnupg && \
  wget -qO- https://dl-ssl.google.com/linux/linux_signing_key.pub | gpg --dearmor -o /usr/share/keyrings/google-chrome.gpg && \
  echo "deb [signed-by=/usr/share/keyrings/google-chrome.gpg] http://dl.google.com/linux/chrome/deb/ stable main" >> /etc/apt/sources.list.d/google.list && \
  apt-get update && \
  apt-get install -y google-chrome-stable && \
  apt-get remove -y ca-certificates wget gnupg && \
  apt-get autoremove -y && \
  rm -rf /var/lib/apt/lists/*; \
fi

RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && \
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

COPY --from=build /app/publish ./
COPY --from=build /app/mcp-server/ ./
COPY --from=frontend-build /app/frontend/dist ./wwwroot
ENV HOME=/home/ild
ENV ILD_DATA_PATH=/data
ENV ILD_WORKTREES_PATH=/worktrees
RUN mkdir -p /data /worktrees && \
  chown -R ild:ild /app /data /worktrees

COPY entrypoint.sh /entrypoint.sh
RUN sed -i 's/\r$//' /entrypoint.sh && chmod +x /entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]
CMD ["dotnet", "ILD.Api.dll"]
