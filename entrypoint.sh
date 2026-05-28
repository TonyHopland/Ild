#!/bin/sh
set -eu

RUNTIME_USER="${RUNTIME_USER:-ild}"
RUNTIME_GROUP="${RUNTIME_GROUP:-ild}"
RUNTIME_DIRS="${RUNTIME_DIRS:-/data /worktrees /home/ild/.agent-config}"

# Agent CLI config dirs (.claude, .opencode, .pi, ...) are kept in a single
# persistent volume mounted at AGENT_CONFIG_STORE, then symlinked into
# $HOME at container start. This lets login state survive image rebuilds
# without freezing tool binary installs that live elsewhere in the image.
# Override AGENT_CONFIG_DIRS / AGENT_CONFIG_FILES to add or remove entries.
#
# AGENT_CONFIG_FILES covers individual files that live in $HOME alongside
# the dotdirs — Claude Code's .claude.json (which holds `oauthAccount` and
# project state) is the canonical example: without it, even a valid
# .claude/.credentials.json reads as logged-out.
AGENT_CONFIG_STORE="${AGENT_CONFIG_STORE:-/home/ild/.agent-config}"
AGENT_CONFIG_DIRS="${AGENT_CONFIG_DIRS:-.claude .opencode .pi}"
AGENT_CONFIG_FILES="${AGENT_CONFIG_FILES:-.claude.json}"

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
# and that $HOME/<name> is a symlink pointing at it. The volume is the source
# of truth across rebuilds. If the image baked in a *real* $HOME/<name>
# directory (e.g. the Claude Code installer creates ~/.claude at build time),
# fold its contents into the store on first run — without overwriting newer
# persisted copies — then replace it with the symlink, so login state written
# later lands in the volume instead of the throwaway container layer.
link_agent_config_dirs() {
  store="$1"
  user_home="$2"
  shift 2

  [ -d "$store" ] || return 0

  for name in "$@"; do
    [ -n "$name" ] || continue
    target="$store/$name"
    link="$user_home/$name"

    mkdir -p "$target"

    if [ -d "$link" ] && [ ! -L "$link" ]; then
      # Image-baked real dir: migrate contents (dotfiles included), keeping any
      # already-persisted file, then drop it so the symlink can take its place.
      cp -an "$link/." "$target/" 2>/dev/null || true
      rm -rf "$link"
    elif [ -e "$link" ] && [ ! -L "$link" ]; then
      rm -f "$link"
    fi

    ln -sfn "$target" "$link"
    chown -R "$RUNTIME_USER:$RUNTIME_GROUP" "$target"
    chown -h "$RUNTIME_USER:$RUNTIME_GROUP" "$link"
  done
}

# For each file name, ensure $HOME/<name> is a symlink at the persistent store.
# On first run we migrate a pre-existing real file into the store so a login
# made before the symlink existed isn't lost. If the store already holds a copy
# it wins (it's the persisted one) and any image-side real file is discarded —
# Claude Code rewrites .claude.json on the next /login regardless.
link_agent_config_files() {
  store="$1"
  user_home="$2"
  shift 2

  [ -d "$store" ] || return 0

  for name in "$@"; do
    [ -n "$name" ] || continue
    target="$store/$name"
    link="$user_home/$name"

    if [ -f "$link" ] && [ ! -L "$link" ] && [ ! -e "$target" ]; then
      mv "$link" "$target"
    elif [ -e "$link" ] && [ ! -L "$link" ]; then
      rm -f "$link"
    fi

    ln -sfn "$target" "$link"
    [ -e "$target" ] && chown "$RUNTIME_USER:$RUNTIME_GROUP" "$target"
    chown -h "$RUNTIME_USER:$RUNTIME_GROUP" "$link"
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

    # Intentional word-split on AGENT_CONFIG_DIRS / AGENT_CONFIG_FILES —
    # entries are space-separated names.
    # shellcheck disable=SC2086
    link_agent_config_dirs "$AGENT_CONFIG_STORE" "$runtime_home" $AGENT_CONFIG_DIRS
    # shellcheck disable=SC2086
    link_agent_config_files "$AGENT_CONFIG_STORE" "$runtime_home" $AGENT_CONFIG_FILES
  fi

  wait_for_postgres
  exec gosu "$RUNTIME_USER:$RUNTIME_GROUP" "$@"
fi

wait_for_postgres
exec "$@"
