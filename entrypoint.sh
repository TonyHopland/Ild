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
#
# The store holds the provider logins and is ILD's alone (0700): agents run in
# containers of their own and reach their model through ILD's gateway, so none
# of them ever reads it (docs/adr/0031-model-gateway.md).
#
# Names may be nested (e.g. .config/opencode): opencode keeps its auth/state under
# the XDG data dir, hence the .config/opencode + .local/share/opencode entries.
AGENT_CONFIG_STORE="${AGENT_CONFIG_STORE:-/home/ild/.agent-config}"
AGENT_CONFIG_DIRS="${AGENT_CONFIG_DIRS:-.claude .opencode .pi .copilot .codex .config/opencode .local/share/opencode}"
AGENT_CONFIG_FILES="${AGENT_CONFIG_FILES:-.claude.json}"

# Own a runtime directory (a volume mount point) by the runtime user. The whole
# tree is re-owned only when something in it belongs to another uid, as on the
# first start after an upgrade from a version whose agents ran as a second uid.
ensure_owned_by_runtime_user() {
  path="$1"

  mkdir -p "$path"

  if find "$path" ! -user "$RUNTIME_USER" -print -quit 2>/dev/null | grep -q .; then
    chown -R "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
  else
    chown "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
  fi
}

# Ensure the parent directory of a nested $HOME link exists and apply the caller's
# OWNER:GROUP spec to *every* level between $HOME (exclusive) and that parent
# (inclusive).
#
# `mkdir -p`, run as root, creates the intermediate levels too — making
# $HOME/.local/share also creates $HOME/.local — so chowning only the immediate
# parent left those intermediates root:root. The runtime user could then traverse
# but not write them, and a later Directory.CreateDirectory($HOME/.local/bin) at
# install time was denied (WI-163). Walking the whole chain covers every nested
# entry, current or future.
ensure_home_link_parent() {
  _home="$1"
  _ownership="$2"        # chown OWNER:GROUP spec applied to each level of the chain
  _link="$3"
  _parent="$(dirname "$_link")"

  [ "$_parent" != "$_home" ] || return 0

  mkdir -p "$_parent"
  _dir="$_parent"
  while [ "$_dir" != "$_home" ] && [ "$_dir" != "/" ]; do
    chown "$_ownership" "$_dir"
    _dir="$(dirname "$_dir")"
  done
}

# For each agent dotdir name, ensure a subdir exists under the config store
# and that $HOME/<name> is a symlink pointing at it. The volume is the source
# of truth across rebuilds. If the image baked in a *real* $HOME/<name>
# directory, fold its contents into the store on first run — without
# overwriting newer persisted copies — then replace it with the symlink, so
# login state written later lands in the volume instead of the container layer.
link_agent_config_dirs() {
  store="$1"
  user_home="$2"
  owner="$3"
  group="$4"
  shift 4

  [ -d "$store" ] || return 0

  for name in "$@"; do
    [ -n "$name" ] || continue
    target="$store/$name"
    link="$user_home/$name"

    mkdir -p "$target"
    # Nested names (.config/opencode) need their parent in $HOME to exist and be
    # writable by the home owner so the CLI can write siblings there.
    ensure_home_link_parent "$user_home" "$owner:$group" "$link"

    migrated=
    if [ -d "$link" ] && [ ! -L "$link" ]; then
      # Image-baked real dir: migrate contents (dotfiles included), keeping any
      # already-persisted file, then drop it so the symlink can take its place.
      cp -an "$link/." "$target/" 2>/dev/null || true
      rm -rf "$link"
      migrated=1
    elif [ -e "$link" ] && [ ! -L "$link" ]; then
      rm -f "$link"
    fi

    ln -sfn "$target" "$link"
    # Only the migration branch brings in files of unknown ownership, so only it
    # needs the recursive pass; doing it on every boot would re-walk the whole
    # store, which holds the .claude/projects transcripts.
    if [ -n "$migrated" ]; then
      chown -R "$owner:$group" "$target"
    else
      chown "$owner:$group" "$target"
    fi
    chown -h "$owner:$group" "$link"
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
  group="$4"
  shift 4

  [ -d "$store" ] || return 0

  for name in "$@"; do
    [ -n "$name" ] || continue
    target="$store/$name"
    link="$user_home/$name"

    ensure_home_link_parent "$user_home" "$owner:$group" "$link"

    if [ -f "$link" ] && [ ! -L "$link" ] && [ ! -e "$target" ]; then
      mv "$link" "$target"
    elif [ -e "$link" ] && [ ! -L "$link" ]; then
      rm -f "$link"
    fi

    ln -sfn "$target" "$link"
    [ -e "$target" ] && chown "$owner:$group" "$target"
    chown -h "$owner:$group" "$link"
  done
}

# The credential store is the runtime user's alone: owned by it, 0700, and rid of
# the group and ACL grants earlier versions gave their agent uid. The tripwire
# keeps the common boot, where nothing drifted, from walking the whole store.
ensure_credential_store() {
  path="$1"
  mkdir -p "$path"

  if find "$path" \( ! -user "$RUNTIME_USER" -o -perm /g+rwxs,o+rwx \) -print -quit 2>/dev/null | grep -q .; then
    if command -v setfacl >/dev/null 2>&1; then
      setfacl -R -b "$path" 2>/dev/null || true
    fi
    chown -R "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
    chmod -R g-rwxs,o-rwx "$path"
  fi

  chown "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
  chmod 0700 "$path"
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

# Whether this container runs in a user namespace (rootless Podman or Docker), where root here is not
# the host's root. The initial namespace maps every uid to itself.
in_user_namespace() {
  [ "$(awk 'NR==1{print $1" "$2" "$3}' "${UID_MAP_FILE:-/proc/self/uid_map}" 2>/dev/null)" != "0 0 4294967295" ]
}

# ILD in its image, outside Kubernetes, runs every run, chat and sign-in in a container, through the engine
# socket mounted at ILD_CONTAINER_SOCKET. Whoever holds it controls the engine; only ILD's process does, and
# no run or chat container is ever given it. gosu rebuilds the supplementary groups from /etc/group when it
# drops to RUNTIME_USER, so a compose group_add is lost; the group is granted here, before the drop.
#  - A socket whose group is not root (a rootful Docker's docker group, a socket an operator gave a group):
#    RUNTIME_USER joins a group of that gid, made if the image has none. Nothing outside changes.
#  - A socket of group root in a user namespace (rootless Podman or Docker: the host user's own socket
#    shows as root:root here): its group becomes ild-engine, which only RUNTIME_USER is in. The host user
#    still owns it, so nothing on the host loses access. A restart of the engine's socket makes a new one,
#    which ILD cannot open until it restarts too; ILD says so in its log and on the settings page.
#  - A socket of group root outside a user namespace is the host's root group (rootful Podman): left alone,
#    and said why, unless the operator sets ILD_CONTAINER_SOCKET_GRANT=1. Then its group becomes
#    RUNTIME_USER's own, on every start, so a socket the engine made anew after a restart is covered too.
grant_engine_socket() {
  # Not ILD's image (the WorkItem Server shares this script), or pods instead of containers.
  [ "${ILD_IMAGE:-}" = 1 ] && [ -z "${KUBERNETES_SERVICE_HOST:-}" ] || return 0
  socket="${ILD_CONTAINER_SOCKET:-}"
  socket="${socket#unix://}"
  if [ -z "$socket" ] || [ ! -S "$socket" ]; then
    echo "WARNING: ILD_CONTAINER_SOCKET=${socket:-(unset)} is not a socket in this container; mount the container engine's socket there. No run can start." >&2
    return 0
  fi

  gid="$(stat -c %g "$socket")"
  if [ "$gid" != 0 ]; then
    group="$(getent group "$gid" | cut -d: -f1)"
    if [ -z "$group" ]; then
      group="ild-engine-$gid"
      groupadd --gid "$gid" "$group"
    fi
  elif in_user_namespace; then
    group=ild-engine
    getent group "$group" >/dev/null || groupadd "$group"
    if ! chgrp "$group" "$socket"; then
      echo "WARNING: could not give $socket to group $group; ILD cannot open it and no run can start." >&2
      return 0
    fi
    gid="$(getent group "$group" | cut -d: -f3)"
  elif [ "${ILD_CONTAINER_SOCKET_GRANT:-}" = 1 ]; then
    # The operator chose it: the socket goes to RUNTIME_USER's own group, whose gid the image fixes (10001),
    # since outside a user namespace the host sees the same number.
    group="$(getent group "$RUNTIME_USER" | cut -d: -f1)"
    gid="$(getent group "$RUNTIME_USER" | cut -d: -f3)"
    if [ -z "$group" ]; then
      echo "WARNING: ILD_CONTAINER_SOCKET_GRANT=1 but this image has no group $RUNTIME_USER; $socket is left alone and no run can start." >&2
      return 0
    fi
    if ! chgrp "$group" "$socket"; then
      echo "WARNING: could not give $socket to group $group; ILD cannot open it and no run can start." >&2
      return 0
    fi
    echo "ILD_CONTAINER_SOCKET_GRANT=1: gave $socket, of the host's root engine, to group $group (gid $gid). ILD is root-equivalent on that host."
  else
    echo "WARNING: $socket belongs to the host's root group, which ILD does not join; no run can start. Set ILD_CONTAINER_SOCKET_GRANT=1 to give it ILD's own group on each start (ILD is then root-equivalent on that host), give it a group of its own (Docker's docker group), or use rootless Podman." >&2
    return 0
  fi

  case "$(stat -c %A "$socket")" in
    ????rw*) ;;
    *) echo "WARNING: $socket is not readable and writable by its group $group; ILD cannot open it and no run can start." >&2 ;;
  esac
  usermod -aG "$group" "$RUNTIME_USER"
  echo "Container engine socket $socket: $RUNTIME_USER can open it through group $group."
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

    # Intentional word-split on AGENT_CONFIG_DIRS / AGENT_CONFIG_FILES:
    # entries are space-separated names.
    # shellcheck disable=SC2086
    link_agent_config_dirs "$AGENT_CONFIG_STORE" "$runtime_home" "$RUNTIME_USER" "$RUNTIME_GROUP" $AGENT_CONFIG_DIRS
    # shellcheck disable=SC2086
    link_agent_config_files "$AGENT_CONFIG_STORE" "$runtime_home" "$RUNTIME_USER" "$RUNTIME_GROUP" $AGENT_CONFIG_FILES
    ensure_credential_store "$AGENT_CONFIG_STORE"
  fi

  grant_engine_socket
  wait_for_postgres
  # The user alone, not user:group: gosu then takes the supplementary groups from /etc/group, which is how
  # ILD keeps the engine socket's group that grant_engine_socket just gave it. With user:group it gets none.
  exec gosu "$RUNTIME_USER" "$@"
fi

wait_for_postgres
exec "$@"
