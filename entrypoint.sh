#!/bin/sh
set -eu

RUNTIME_USER="${RUNTIME_USER:-ild}"
RUNTIME_GROUP="${RUNTIME_GROUP:-ild}"
RUNTIME_DIRS="${RUNTIME_DIRS:-/data /worktrees /home/ild/.agent-config}"

# Agent CLI config dirs (.claude, .opencode, .pi, ...) are kept in a single
# persistent volume mounted at AGENT_CONFIG_STORE, then symlinked into
# $HOME at container start. This lets login state survive image rebuilds
# without freezing tool binary installs that live elsewhere in the image.
# Override AGENT_CONFIG_DIRS to add or remove agents.
AGENT_CONFIG_STORE="${AGENT_CONFIG_STORE:-/home/ild/.agent-config}"
AGENT_CONFIG_DIRS="${AGENT_CONFIG_DIRS:-.claude .opencode .pi}"

ensure_owned_by_runtime_user() {
  path="$1"

  mkdir -p "$path"

  if find "$path" ! -user "$RUNTIME_USER" -print -quit 2>/dev/null | grep -q .; then
    chown -R "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
  else
    chown "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
  fi
}

# For each agent dotdir name, ensure a subdir exists under the config store
# and that $HOME/<name> points at it via a symlink. Skips entries that
# already exist as real directories in $HOME so we don't clobber image-side
# state that the container expects to find there.
link_agent_config_dirs() {
  store="$1"
  user_home="$2"
  shift 2

  [ -d "$store" ] || return 0

  for name in "$@"; do
    [ -n "$name" ] || continue
    target="$store/$name"
    link="$user_home/$name"

    if [ ! -d "$target" ]; then
      mkdir -p "$target"
      chown "$RUNTIME_USER:$RUNTIME_GROUP" "$target"
    fi

    if [ -L "$link" ] || [ ! -e "$link" ]; then
      ln -sfn "$target" "$link"
      chown -h "$RUNTIME_USER:$RUNTIME_GROUP" "$link"
    fi
  done
}

wait_for_postgres() {
  # Pick the connection string for this container (ILD or WorkItemServer)
  conn_str="${ILD_DB_CONNECTION_STRING:-${WORKITEM_DB_CONNECTION_STRING:-}}"

  # If no connection string found, skip wait
  if [ -z "$conn_str" ]; then
    return 0
  fi

  # Extract host and port from connection string (format: Host=X;Port=Y;...)
  host=$(echo "$conn_str" | sed -n 's/.*Host=\([^;]*\).*/\1/p')
  port=$(echo "$conn_str" | sed -n 's/.*Port=\([^;]*\).*/\1/p')

  if [ -z "$host" ]; then
    return 0
  fi

  port="${port:-5432}"
  max_retries=30
  retry_interval=2

  echo "Waiting for PostgreSQL at ${host}:${port}..."
  i=1
  while [ "$i" -le "$max_retries" ]; do
    if command -v nc >/dev/null 2>&1; then
      nc -z "$host" "$port" 2>/dev/null && return 0
    elif command -v pg_isready >/dev/null 2>&1; then
      pg_isready -h "$host" -p "$port" 2>/dev/null && return 0
    else
      # Fallback: try to open a TCP connection using a subshell and /dev/tcp
      # This works in bash and ash/dash on Debian-based images
      if (echo >/dev/tcp/"$host"/"$port") 2>/dev/null; then
        return 0
      fi
    fi
    sleep "$retry_interval"
    i=$((i + 1))
  done

  echo "Warning: PostgreSQL at ${host}:${port} did not become available after $((max_retries * retry_interval))s"
  return 0
}

if [ "$(id -u)" -eq 0 ] && id "$RUNTIME_USER" >/dev/null 2>&1; then
  for path in $RUNTIME_DIRS; do
    ensure_owned_by_runtime_user "$path"
  done

  runtime_home="$(getent passwd "$RUNTIME_USER" | cut -d: -f6)"
  if [ -n "$runtime_home" ]; then
    mkdir -p "$runtime_home"
    chown "$RUNTIME_USER:$RUNTIME_GROUP" "$runtime_home"
    export HOME="$runtime_home"

    # Intentional word-split on AGENT_CONFIG_DIRS — entries are
    # space-separated dotdir names.
    # shellcheck disable=SC2086
    link_agent_config_dirs "$AGENT_CONFIG_STORE" "$runtime_home" $AGENT_CONFIG_DIRS
  fi

  wait_for_postgres
  exec gosu "$RUNTIME_USER:$RUNTIME_GROUP" "$@"
fi

wait_for_postgres
exec "$@"
