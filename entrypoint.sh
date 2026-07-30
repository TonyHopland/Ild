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
#   * DATA_TRAVERSE_DIRS (/data) stay ild-private but traversable by SHARED_GROUP,
#     so the agent can reach the shared subtrees by exact path without reading
#     secrets. Reach is granted through the group throughout — no "other" bits.
#   * The orchestrator is dropped WITH ambient RUNTIME_AMBIENT_CAPS so it — and
#     only it — can drop the agent to AGENT_USER via setpriv.
# All unset => single-uid mode, unchanged (Dockerfile.WorkItemServer).
AGENT_USER="${AGENT_USER:-}"
AGENT_GROUP="${AGENT_GROUP:-${AGENT_USER}}"
AGENT_HOME="${AGENT_HOME:-}"
AGENT_SCRATCH_DIR="${AGENT_SCRATCH_DIR:-}"
ORCHESTRATOR_PRIVATE_DIR="${ORCHESTRATOR_PRIVATE_DIR:-}"
SHARED_GROUP="${SHARED_GROUP:-}"
RUNTIME_AMBIENT_CAPS="${RUNTIME_AMBIENT_CAPS:-}"
SHARED_RW_DIRS="${SHARED_RW_DIRS:-}"
SHARED_RO_DIRS="${SHARED_RO_DIRS:-}"
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
#
# Names may be nested (e.g. .config/opencode): splitting HOME between the two
# uids means anything a CLI keeps under an XDG path has to be shared explicitly,
# or the agent would see an empty home even though the ild-side terminal login
# succeeded. opencode keeps its auth/state under the XDG data dir, hence the
# .config/opencode + .local/share/opencode entries.
AGENT_CONFIG_STORE="${AGENT_CONFIG_STORE:-/home/ild/.agent-config}"
AGENT_CONFIG_DIRS="${AGENT_CONFIG_DIRS:-.claude .opencode .pi .copilot .config/opencode .local/share/opencode}"
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
# group-owned by SHARED_GROUP, every directory setgid (so new entries keep
# inheriting the group) and carrying a default POSIX ACL granting the group rwx
# on everything created later.
#
# The invariant is deliberately about GROUP and MODE, not owner: files in a
# shared tree legitimately belong to whichever uid created them (the agent owns
# what it writes into its worktree). Including the owner would both fire the
# expensive repair on every restart once the agent has written anything, and
# make the repair seize the agent's files — so the repair is a `chgrp`, and only
# the root of the tree gets its owner normalized.
#
# The recursive repair runs only when the tripwire below finds real drift (first
# run, a wrong group, a directory that lost setgid/group-rwx, or a file that lost
# group-read), so steady-state startups stay cheap even on a large worktree/repo
# tree. The tripwire is a cheap check, not an exhaustive audit: it deliberately
# does not require group-write on every file, because git creates loose objects
# read-only (0444) by design and that must not trigger a full re-walk each boot.
ensure_shared_rw() {
  path="$1"

  mkdir -p "$path"

  if find "$path" \( \
        ! -group "$SHARED_GROUP" \
        -o \( -type d ! -perm -2070 \) \
        -o \( -type f ! -perm -040 \) \
      \) -print -quit 2>/dev/null | grep -q .; then
    chgrp -R "$SHARED_GROUP" "$path"
    find "$path" -type d -exec chmod g+rwxs {} + 2>/dev/null || true
    find "$path" -type f -exec chmod g+rw {} + 2>/dev/null || true
    if command -v setfacl >/dev/null 2>&1; then
      setfacl -R -m g:"$SHARED_GROUP":rwX "$path" 2>/dev/null || true
      # Default ACLs only apply to directories; set them per-dir to avoid errors.
      find "$path" -type d -exec setfacl -d -m g:"$SHARED_GROUP":rwx {} + 2>/dev/null || true
    fi
  fi

  # Cheap and idempotent: the root of the shared tree is always normalized.
  # 2770, not 2775: access to this tree is a GROUP grant, and reaching anything
  # inside requires traversing this root — so denying "other" here is what makes
  # the grant group-only, whatever the modes on individual entries beneath say.
  chown "$RUNTIME_USER:$SHARED_GROUP" "$path"
  chmod 2770 "$path"
}

# Like ensure_shared_rw, but the group only gets read/execute. Used for the
# managed agent installs: the agent must be able to exec those CLIs, but the
# orchestrator runs the same binaries as itself (version checks, the provider
# terminal), so letting the agent rewrite them would hand it a way back across
# the boundary. The owner (orchestrator) still writes, so installs/updates work.
ensure_shared_ro() {
  path="$1"

  mkdir -p "$path"

  # Unlike the read/write tripwire this must also catch EXCESS permission, not
  # just missing permission: the agent CLIs are installed onto /data at runtime
  # (npm, as the orchestrator, under the umask 002 set below), which leaves the
  # tree group-writable. A missing-only check reports that state as clean — 2775
  # contains 2050 and 0775 contains 040 — so g-w would never be applied and the
  # agent could rewrite the very binaries the orchestrator later execs as itself.
  # The group-write clause is restricted to regular files and directories on
  # purpose: symlinks are always mode 0777 (npm fills node_modules/.bin with
  # them) and chmod cannot change that, so including them would report drift
  # forever and re-walk the whole tree on every boot.
  if find "$path" \( \
        ! -group "$SHARED_GROUP" \
        -o \( -type d ! -perm -2050 \) \
        -o \( -type f ! -perm -040 \) \
        -o \( \( -type d -o -type f \) -perm -020 \) \
      \) -print -quit 2>/dev/null | grep -q .; then
    chgrp -R "$SHARED_GROUP" "$path"
    # Directories keep group r-x + setgid; files gain group read but never group
    # execute — an already-executable binary keeps the group x it came with, so
    # the agent can still exec the CLI it must not be able to modify.
    find "$path" -type d -exec chmod g+rxs,g-w {} + 2>/dev/null || true
    find "$path" -type f -exec chmod g+r,g-w {} + 2>/dev/null || true
    if command -v setfacl >/dev/null 2>&1; then
      setfacl -R -m g:"$SHARED_GROUP":rX "$path" 2>/dev/null || true
      find "$path" -type d -exec setfacl -d -m g:"$SHARED_GROUP":rx {} + 2>/dev/null || true
    fi
  fi

  chown "$RUNTIME_USER:$SHARED_GROUP" "$path"
  chmod 2750 "$path"
}

# Orchestrator-only state: owned by the runtime user, owner-only, and created
# HERE — before any agent-uid process can run. That ordering is the point. The
# askpass helper git is handed (with the repository token in its environment) and
# the preview state dir both live at fixed, guessable paths; if the agent could
# create one of those paths first, orchestrator code that only writes the file
# "if it is missing" would execute the agent's version as the orchestrator.
# Pre-creating the root at 0700 closes that regardless of how guessable the paths
# beneath it are, which is why this can stay on /tmp and remain ephemeral rather
# than accumulating on the data volume.
ensure_private() {
  path="$1"
  mkdir -p "$path"
  chown -R "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
  chmod 0700 "$path"
}

# Keep a private directory (e.g. /data holding secrets) owned by the runtime user
# but traversable by the shared group, so the agent uid can reach the shared
# subtrees beneath it by exact path without being able to list it or read private
# sibling files.
#
# 0710 with the shared group rather than 0711: --x is the same reach for the
# agent either way, but granting it through the group keeps it scoped to the two
# uids that are meant to have it instead of to everyone. Deliberately NOT setgid —
# files the orchestrator writes directly here must keep its own group, or private
# state would drift into the shared one.
ensure_traverse() {
  path="$1"
  mkdir -p "$path"
  chown "$RUNTIME_USER:$SHARED_GROUP" "$path"
  chmod 0710 "$path"
}

# Ensure the parent directory of a nested $HOME link exists and apply the caller's
# OWNER:GROUP spec to *every* level between $HOME (exclusive) and that parent
# (inclusive).
#
# `mkdir -p`, run as root, creates the intermediate levels too — making
# $HOME/.local/share also creates $HOME/.local — so chowning only the immediate
# parent left those intermediates root:root. The orchestrator could then traverse
# but not write them, and a later Directory.CreateDirectory($HOME/.local/bin) at
# install time was denied (WI-163). Walking the whole chain covers every nested
# entry, current or future.
#
# The chain keeps whatever mode `mkdir -p` gave it. This matters only in two-uid
# mode, where the entrypoint sets umask 002 so those dirs are 0775 (group-
# writable) — then the GROUP in the spec, not the mode, decides who else may
# write. Callers there pass the home owner's *private* group (see link_agent_
# config_dirs), never the shared agent group, or the agent uid would gain write on
# the orchestrator's home scaffolding (ADR-0014). Single-uid mode has no separate
# agent uid and does not set that umask, so the exposure cannot arise (and the
# private and store groups coincide there anyway).
#
# The 4th arg opts into best-effort chowns (`|| true`). The entrypoint runs under
# `set -e`, so the default strict chown aborts the boot if it fails — which is
# what we want for the ORCHESTRATOR home, where a silently-skipped chown would
# re-open the exact WI-163 failure. The secondary (agent) home passes best-effort
# to preserve its original tolerant behavior: it is populated as pure convenience
# right after agent_home itself is chowned strictly, so a failure there should not
# be able to take the whole container down.
ensure_home_link_parent() {
  _home="$1"
  _ownership="$2"        # chown OWNER:GROUP spec applied to each level of the chain
  _link="$3"
  _best_effort="${4:-}"  # non-empty => tolerate chown failures (`|| true`)
  _parent="$(dirname "$_link")"

  [ "$_parent" != "$_home" ] || return 0

  mkdir -p "$_parent"
  _dir="$_parent"
  while [ "$_dir" != "$_home" ] && [ "$_dir" != "/" ]; do
    if [ -n "$_best_effort" ]; then
      chown "$_ownership" "$_dir" 2>/dev/null || true
    else
      chown "$_ownership" "$_dir"
    fi
    _dir="$(dirname "$_dir")"
  done
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
  group="$4"        # shared group for the store target + symlink (both uids share it)
  home_group="$5"   # owner's private group for the $HOME parent-chain scaffolding
  shift 5

  [ -d "$store" ] || return 0

  for name in "$@"; do
    [ -n "$name" ] || continue
    target="$store/$name"
    link="$user_home/$name"

    mkdir -p "$target"
    # Nested names (.config/opencode) need their parent in $HOME to exist and be
    # writable by the home owner so the CLI can write siblings there. Own the
    # chain with the orchestrator's PRIVATE group ($home_group, e.g. ild:ild), not
    # the shared store $group: in two-uid mode these dirs are 0775 (umask 002), so
    # the shared group would hand the agent uid write on $HOME/.local — the parent of
    # $HOME/.local/bin, which BuildDefaultEnvironment prepends to the PATH of
    # preview steps that run as the orchestrator. That is the cross-uid tool
    # shadowing ADR-0014 exists to prevent. The store target + symlink below keep
    # the shared $group so both uids share the one credential store.
    ensure_home_link_parent "$user_home" "$owner:$home_group" "$link"

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
    # needs the recursive pass. Doing it unconditionally re-walked the whole store
    # (which holds the .claude/projects transcripts) on every boot — defeating the
    # tripwire optimization in ensure_shared_rw below — and seized files the agent
    # had created back to the orchestrator, which its own `chgrp`-only repair is
    # careful not to do.
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
  group="$4"        # shared group for the store target + symlink
  home_group="$5"   # owner's private group for the $HOME parent chain (see link_agent_config_dirs)
  shift 5

  [ -d "$store" ] || return 0

  for name in "$@"; do
    [ -n "$name" ] || continue
    target="$store/$name"
    link="$user_home/$name"

    ensure_home_link_parent "$user_home" "$owner:$home_group" "$link"

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
    # best_effort: this agent-home scaffolding is convenience, not the WI-163 fix,
    # and its symlink chown below is likewise tolerant — keep both from aborting boot.
    ensure_home_link_parent "$home" "$owner:$owner" "$link" best_effort
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
  if [ -n "$AGENT_USER" ]; then
    # Fail loudly rather than degrading. Silently falling back to the gosu drop
    # here would leave the orchestrator without the ambient capabilities while the
    # app still routes every agent launch through setpriv — so isolation would be
    # off AND every run would fail with an opaque EPERM. The gosu form would also
    # drop the supplementary groups the shared dirs depend on.
    if [ -z "$SHARED_GROUP" ]; then
      echo "FATAL: AGENT_USER=$AGENT_USER but SHARED_GROUP is empty; every shared-directory repair would silently no-op and the agent would have no access to the worktree. Unset AGENT_USER to run single-uid." >&2
      exit 1
    fi
    if [ -z "$RUNTIME_AMBIENT_CAPS" ]; then
      echo "FATAL: AGENT_USER=$AGENT_USER but RUNTIME_AMBIENT_CAPS is empty; the orchestrator could not spawn the agent. Unset AGENT_USER to run single-uid." >&2
      exit 1
    fi
    for tool in capsh setpriv; do
      if ! command -v "$tool" >/dev/null 2>&1; then
        echo "FATAL: AGENT_USER=$AGENT_USER requires '$tool' (capsh: libcap2-bin, setpriv: util-linux). Unset AGENT_USER to run single-uid." >&2
        exit 1
      fi
    done

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
    # Group-writable by default. Where a default ACL is in effect it already
    # grants the shared group rwx (the umask is ignored for those paths), but the
    # setfacl calls are best-effort — a volume filesystem without ACL support
    # would silently leave the agent read-only on files the orchestrator creates.
    # umask 002 makes the setgid + shared-group scheme work on its own. Files the
    # orchestrator writes under the private /data are unaffected in practice:
    # they stay group `ild`, which the agent is not a member of.
    umask 002

    # The app reads ILD_AGENT_* to decide whether (and to whom) it drops each
    # agent-CLI launch. Derive them here rather than setting them in the image, so
    # AGENT_USER is the single switch: clearing it turns isolation off for BOTH
    # the shell-side setup and the app, instead of leaving the app routing
    # launches through setpriv without the caps or shared dirs to back it.
    export ILD_AGENT_USER="$AGENT_USER"
    export ILD_AGENT_GROUP="$AGENT_GROUP"
    export ILD_AGENT_HOME="$AGENT_HOME"
    export ILD_AGENT_SCRATCH_ROOT="$AGENT_SCRATCH_DIR"
    export ILD_ORCHESTRATOR_PRIVATE_ROOT="$ORCHESTRATOR_PRIVATE_DIR"

    # Two-uid mode. Order matters: the private roots (/data) are created and
    # locked to traverse-only FIRST, so that the shared subtrees created beneath
    # them in the next loop land inside an already-correct parent rather than
    # having /data implicitly created with default ownership by a `mkdir -p`.
    for path in $DATA_TRAVERSE_DIRS; do
      ensure_traverse "$path"
    done
    for path in $SHARED_RW_DIRS; do
      ensure_shared_rw "$path"
    done
    for path in $SHARED_RO_DIRS; do
      ensure_shared_ro "$path"
    done
    [ -n "$ORCHESTRATOR_PRIVATE_DIR" ] && ensure_private "$ORCHESTRATOR_PRIVATE_DIR"
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

    # In two-uid mode the store lives under the runtime user's home and the
    # agent's dotdirs are symlinks into it, so the agent uid must be able to
    # traverse this home. useradd's Debian default is 0750 group ild, which the
    # agent is not in; the fix is the GROUP, so below this home becomes 0710 group
    # ild-agents — traversal only (never listing), and never through world bits.
    config_group="$RUNTIME_GROUP"
    if [ -n "$AGENT_USER" ]; then
      # 0710 + shared group: the agent must traverse this home to resolve the
      # credential-store and .gitconfig symlinks, but it never needs to list it,
      # and no one outside the two uids needs anything here.
      chown "$RUNTIME_USER:$SHARED_GROUP" "$runtime_home"
      chmod 0710 "$runtime_home"
      [ -n "$SHARED_GROUP" ] && config_group="$SHARED_GROUP"
    fi

    # Intentional word-split on AGENT_CONFIG_DIRS / AGENT_CONFIG_FILES —
    # entries are space-separated names. $config_group is the shared store group
    # (SHARED_GROUP under isolation); $RUNTIME_GROUP is the orchestrator's own
    # private group, used for the $HOME parent-chain scaffolding so the agent uid
    # never gains write on it (single-uid: the two groups are identical anyway).
    # shellcheck disable=SC2086
    link_agent_config_dirs "$AGENT_CONFIG_STORE" "$runtime_home" "$RUNTIME_USER" "$config_group" "$RUNTIME_GROUP" $AGENT_CONFIG_DIRS
    # shellcheck disable=SC2086
    link_agent_config_files "$AGENT_CONFIG_STORE" "$runtime_home" "$RUNTIME_USER" "$config_group" "$RUNTIME_GROUP" $AGENT_CONFIG_FILES

    if [ -n "$AGENT_USER" ] && [ -n "$AGENT_HOME" ]; then
      # The link helpers above already applied SHARED_GROUP, so this pass only
      # has to add the setgid bits + default ACL, and its drift tripwire is no
      # longer guaranteed to fire every boot. It is not guaranteed to stay quiet
      # either: the tripwire treats any 0600 file as drift, and these CLIs rewrite
      # .credentials.json at 0600 on each token refresh, so a boot that follows a
      # refresh still re-walks the store.
      [ -n "$SHARED_GROUP" ] && ensure_shared_rw "$AGENT_CONFIG_STORE"

      agent_home="$(getent passwd "$AGENT_USER" | cut -d: -f6)"
      agent_home="${agent_home:-$AGENT_HOME}"
      mkdir -p "$agent_home"
      chown "$AGENT_USER:$SHARED_GROUP" "$agent_home"
      chmod 0750 "$agent_home"

      # shellcheck disable=SC2086
      link_secondary_home "$agent_home" "$AGENT_USER" "$AGENT_CONFIG_STORE" $AGENT_CONFIG_DIRS $AGENT_CONFIG_FILES

      # The npm global prefix a Worktree Preview's install steps use. The preview
      # runs as the agent (ADR-0016), so `npm install -g` writes here and the
      # agent-uid Cmd nodes and CLI adapters that follow exec from here. It has to
      # be created now, as root, and owned by the agent: the orchestrator prepends
      # $AGENT_HOME/.local/bin to its own PATH and would otherwise create it
      # itself, leaving a prefix owned by a uid the agent is not — and npm would
      # fail on it. `mkdir -p` also covers .local, which link_secondary_home may
      # already have made for .local/share/opencode.
      mkdir -p "$agent_home/.local/bin"
      chown "$AGENT_USER:$AGENT_USER" "$agent_home/.local" "$agent_home/.local/bin"

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
