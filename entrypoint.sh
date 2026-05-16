#!/bin/sh
set -eu

RUNTIME_USER="${RUNTIME_USER:-ild}"
RUNTIME_GROUP="${RUNTIME_GROUP:-ild}"
RUNTIME_DIRS="${RUNTIME_DIRS:-/data /worktrees}"

ensure_owned_by_runtime_user() {
  path="$1"

  mkdir -p "$path"

  if find "$path" ! -user "$RUNTIME_USER" -print -quit 2>/dev/null | grep -q .; then
    chown -R "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
  else
    chown "$RUNTIME_USER:$RUNTIME_GROUP" "$path"
  fi
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
  fi

  exec gosu "$RUNTIME_USER:$RUNTIME_GROUP" "$@"
fi

exec "$@"
