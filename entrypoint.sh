#!/bin/sh
set -eu

RUNTIME_USER="${RUNTIME_USER:-ild}"
RUNTIME_GROUP="${RUNTIME_GROUP:-ild}"
RUNTIME_DIRS="${RUNTIME_DIRS:-/data /worktrees /home/ild/.agent-config}"

# uid isolation (docs/adr/0014-agent-uid-isolation.md). When AGENT_USER is set the
# coding-agent CLI runs as a second, lower-trust uid instead of sharing the
# orchestrator's. In that mode:
#   * SHARED_RW_DIRS are group-owned by SHARED_GROUP, setgid, and default-ACL'd so
#     files created by either uid stay read/write for the other (worktrees, the
#     /data agent installs + repo store, the credential store).
#   * DATA_TRAVERSE_DIRS (/data) stay ild-private but world-traversable, so the
#     agent can reach the shared subtrees by exact path without reading secrets.
#   * The orchestrator is dropped WITH ambient RUNTIME_AMBIENT_CAPS so it — and
#     only it — can drop the agent to AGENT_USER via setpriv.
# All unset => single-uid mode, unchanged (Dockerfile.WorkItemServer).
AGENT_USER="${AGENT_USER:-}"
AGENT_GROUP="${AGENT_GROUP:-${AGENT_USER}}"
AGENT_HOME="${AGENT_HOME:-}"
SHARED_GROUP="${SHARED_GROUP:-}"
RUNTIME_AMBIENT_CAPS="${RUNTIME_AMBIENT_CAPS:-}"
SHARED_RW_DIRS="${SHARED_RW_DIRS:-}"
DATA_TRAVERSE_DIRS="${DATA_TRAVERSE_DIRS:-}"

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
#
# Under uid isolation the store is group-shared and symlinked into BOTH the
# orchestrator's and the agent's home, so login (run as the orchestrator in the
# provider terminal) and the agent run (as AGENT_USER) see one credential store.
AGENT_CONFIG_STORE="${AGENT_CONFIG_STORE:-/home/ild/.agent-config}"
AGENT_CONFIG_DIRS="${AGENT_CONFIG_DIRS:-.claude .opencode .pi .copilot}"
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

# Make a path shared read/write between the orchestrator and the agent uid:
# owned by the runtime user, group-owned by SHARED_GROUP, setgid (new entries
# inherit the group) and carrying a default POSIX ACL that grants the group rwx
# on everything created later. The recursive fix-up runs only when something is
# out of place (first run, or a stray-owned file) so steady-state startups stay
# cheap even on a large worktree/repo tree.
ensure_shared_rw() {
  path="$1"

  mkdir -p "$path"

  if find "$path" \( ! -user "$RUNTIME_USER" -o ! -group "$SHARED_GROUP" \) -print -quit 2>/dev/null | grep -q .; then
    chown -R "$RUNTIME_USER:$SHARED_GROUP" "$path"
    find "$path" -type d -exec chmod 2775 {} + 2>/dev/null || true
    find "$path" -type f -exec chmod g+rw {} + 2>/dev/null || true
    if command -v setfacl >/dev/null 2>&1; then
      setfacl -R -m g:"$SHARED_GROUP":rwX "$path" 2>/dev/null || true
      # Default ACLs only apply to directories; set them per-dir to avoid errors.
      find "$path" -type d -exec setfacl -d -m g:"$SHARED_GROUP":rwx {} + 2>/dev/null || true
    fi
  else
    chown "$RUNTIME_USER:$SHARED_GROUP" "$path"
    chmod 2775 "$path"
  fi
}

# Keep a private directory (e.g. /data holding secrets) owned by the runtime user
# but world-traversable, so the agent uid can reach the shared subtrees beneath it
# by exact path without being able to list it or read private sibling files.
ensure_traverse() {
  path="$1"
  mkdir -p "$path"
  chown "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
  chmod 0711 "$path"
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
  owner="$3"
  shift 3

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
    chown -R "$owner:$RUNTIME_GROUP" "$target"
    chown -h "$owner:$RUNTIME_GROUP" "$link"
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
  owner="$3"
  shift 3

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
    [ -e "$target" ] && chown "$owner:$RUNTIME_GROUP" "$target"
    chown -h "$owner:$RUNTIME_GROUP" "$link"
  done
}

# Point a second home's dotdirs/dotfiles at the same store (no migration — the
# primary home already populated it). Used for the agent user's home so it reads
# the one shared credential store the orchestrator's login writes to.
link_secondary_home() {
  home="$1"
  owner="$2"
  store="$3"
  shift 3

  [ -d "$store" ] || return 0
  mkdir -p "$home"

  for name in "$@"; do
    [ -n "$name" ] || continue
    link="$home/$name"
    if [ -e "$link" ] && [ ! -L "$link" ]; then
      rm -rf "$link"
    fi
    ln -sfn "$store/$name" "$link"
    chown -h "$owner:$owner" "$link" 2>/dev/null || true
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

# Drop from root to the runtime user and exec the app. Under uid isolation the
# orchestrator keeps ambient RUNTIME_AMBIENT_CAPS (via capsh --keep) so it can
# later drop the agent CLI to AGENT_USER under no_new_privs; otherwise a plain
# gosu drop (no retained capabilities) is used.
drop_and_exec() {
  if [ -n "$AGENT_USER" ] && [ -n "$RUNTIME_AMBIENT_CAPS" ] && command -v capsh >/dev/null 2>&1; then
    amb=""
    inh=""
    for cap in $(echo "$RUNTIME_AMBIENT_CAPS" | tr ',' ' '); do
      amb="$amb --addamb=$cap"
      inh="${inh:+$inh,}$cap"
    done
    # --keep=1 preserves the caps across the setuid; --inh puts them in the
    # inheritable set; --addamb raises them ambient so they survive the agent's
    # exec. The `-- -c 'exec "$@"' -- "$@"` form execs the app via bash.
    # shellcheck disable=SC2086
    exec capsh --keep=1 --user="$RUNTIME_USER" --inh="$inh" $amb -- -c 'exec "$@"' -- "$@"
  fi

  exec gosu "$RUNTIME_USER:$RUNTIME_GROUP" "$@"
}

if [ "$(id -u)" -eq 0 ] && id "$RUNTIME_USER" >/dev/null 2>&1; then
  if [ -n "$AGENT_USER" ]; then
    # Two-uid mode: shared paths first, then lock /data down to traverse-only.
    for path in $DATA_TRAVERSE_DIRS; do
      ensure_traverse "$path"
    done
    for path in $SHARED_RW_DIRS; do
      ensure_shared_rw "$path"
    done
  else
    for path in $RUNTIME_DIRS; do
      ensure_owned_by_runtime_user "$path"
    done
  fi

  runtime_home="$(getent passwd "$RUNTIME_USER" | cut -d: -f6)"
  if [ -n "$runtime_home" ]; then
    mkdir -p "$runtime_home"
    chown "$RUNTIME_USER:$RUNTIME_GROUP" "$runtime_home"
    export HOME="$runtime_home"

    # Intentional word-split on AGENT_CONFIG_DIRS / AGENT_CONFIG_FILES —
    # entries are space-separated names.
    # shellcheck disable=SC2086
    link_agent_config_dirs "$AGENT_CONFIG_STORE" "$runtime_home" "$RUNTIME_USER" $AGENT_CONFIG_DIRS
    # shellcheck disable=SC2086
    link_agent_config_files "$AGENT_CONFIG_STORE" "$runtime_home" "$RUNTIME_USER" $AGENT_CONFIG_FILES

    if [ -n "$AGENT_USER" ] && [ -n "$AGENT_HOME" ]; then
      # Re-share the store: the link helpers above chown it to the runtime
      # group; ensure_shared_rw restores SHARED_GROUP + the default ACL so the
      # agent uid can read/write the credentials too.
      [ -n "$SHARED_GROUP" ] && ensure_shared_rw "$AGENT_CONFIG_STORE"

      agent_home="$(getent passwd "$AGENT_USER" | cut -d: -f6)"
      agent_home="${agent_home:-$AGENT_HOME}"
      mkdir -p "$agent_home"
      chown "$AGENT_USER:$AGENT_USER" "$agent_home"
      chmod 0755 "$agent_home"

      # shellcheck disable=SC2086
      link_secondary_home "$agent_home" "$AGENT_USER" "$AGENT_CONFIG_STORE" $AGENT_CONFIG_DIRS $AGENT_CONFIG_FILES

      # Give the agent git the orchestrator's mounted commit identity: its own
      # home has no .gitconfig, so point one at the read-only mounted file.
      if [ -e "$runtime_home/.gitconfig" ] || [ -L "$runtime_home/.gitconfig" ]; then
        ln -sfn "$runtime_home/.gitconfig" "$agent_home/.gitconfig"
        chown -h "$AGENT_USER:$AGENT_USER" "$agent_home/.gitconfig" 2>/dev/null || true
      fi
    fi
  fi

  wait_for_postgres
  drop_and_exec "$@"
fi

wait_for_postgres
exec "$@"
