ARG NODE_VERSION=22-alpine
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
COPY ILD.sln ./
COPY ILD.Data/ILD.Data.csproj ILD.Data/
COPY ILD.Core/ILD.Core.csproj ILD.Core/
COPY ILD.Api/ILD.Api.csproj ILD.Api/
COPY ILD.Tests/ILD.Tests.csproj ILD.Tests/
COPY ILD.McpServer/ILD.McpServer.csproj ILD.McpServer/
COPY ILD.WorkItemServer/ILD.WorkItemServer.csproj ILD.WorkItemServer/
RUN dotnet restore
COPY . .
WORKDIR /src/ILD.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && \
    mkdir -p /usr/local/share/ca-certificates && \
    rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish ./
COPY --from=frontend-build /app/frontend/dist ./wwwroot
ENV ILD_DATA_PATH=/data
ENV ILD_WORKTREES_PATH=/worktrees
RUN mkdir -p /data /worktrees

ARG WITH_OPENCODE=0
# The opencode install script drops the binary into $HOME/.opencode/bin and
# only updates interactive shell rc files. ILD launches opencode via
# Process.Start, which inherits the container's non-interactive PATH, so we
# additionally symlink the binary into /usr/local/bin so it is discoverable
# without relying on shell rc evaluation.
RUN if [ "$WITH_OPENCODE" = "1" ]; then \
      apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && \
      curl -fsSL https://opencode.ai/install | bash && \
      ln -sf /root/.opencode/bin/opencode /usr/local/bin/opencode && \
      rm -rf /var/lib/apt/lists/*; \
    fi
ENV PATH="/root/.opencode/bin:${PATH}"

ARG WITH_NODE=0
ARG NODE_RUNTIME_VERSION=24.15.0
RUN if [ "$WITH_NODE" = "1" ]; then \
  apt-get update && \
  apt-get install -y ca-certificates curl xz-utils && \
  if [ "$NODE_RUNTIME_VERSION" = "latest" ]; then \
    NODE_RUNTIME_VERSION=$(curl -fsSL https://nodejs.org/dist/index.json | sed -n 's/.*"version":"\(v[^"]*\)".*/\1/p' | head -n1 | sed 's/^v//'); \
  fi && \
  curl -fsSL "https://nodejs.org/dist/v${NODE_RUNTIME_VERSION}/node-v${NODE_RUNTIME_VERSION}-linux-x64.tar.xz" -o node.tar.xz && \
  tar -xf node.tar.xz -C /usr/local --strip-components=1 && \
  rm node.tar.xz && \
  if command -v corepack >/dev/null 2>&1; then corepack enable; else npm install -g pnpm@latest; fi && \
  apt-get remove -y ca-certificates curl && \
  apt-get autoremove -y && \
  rm -rf /var/lib/apt/lists/*; \
fi

ARG WITH_DOTNET_SDK=0
ARG DOTNET_SDK_CHANNEL=10.0
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

ARG WITH_CHROME=0
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

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
CMD ["dotnet", "ILD.Api.dll"]
