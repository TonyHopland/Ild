#!/usr/bin/env bash
#
# Regression test for entrypoint.sh's config-store linking (WI-163).
#
# Bug: starting a work item failed at the Start node's install step with
#   ild.config install failed: Access to the path '/home/ild/.local/bin' is denied.
#
# For a *nested* AGENT_CONFIG_DIRS entry such as `.local/share/opencode`,
# link_agent_config_dirs runs `mkdir -p $HOME/.local/share` as root, which also
# creates the intermediate `$HOME/.local`. Chowning only the immediate parent left
# `$HOME/.local` root-owned, so ILD could not create `$HOME/.local/bin` later.
#
# This test sources the real link helper, records the owner and group of every
# path `chown` touches, and asserts the FULL intermediate chain of each nested
# link, not just the immediate parent, ends up owned by ILD's user and group.
#
# Runs unprivileged: the chown stub models ownership, so the invariant is checked
# purely from what the script asks for.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
entrypoint="$repo_root/entrypoint.sh"

owner="ild"   # the uid the install step runs as
group="ild"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

# --- Load the real link helper without running the entrypoint's top-level body.
# link_agent_config_dirs delegates the parent-chain chown to ensure_home_link_parent
# (the helper that carries the fix), so both are sourced: the test
# exercises the shipped implementation, not a copy of it. Each function is
# extracted from its header line to its first column-0 `}` (an exact match, so it
# is robust to line-number drift and to `}` appearing mid-body).
funcs="$(mktemp)"
trap 'rm -f "$funcs"' EXIT
for fn in ensure_home_link_parent link_agent_config_dirs; do
  awk -v hdr="$fn() {" '$0==hdr{p=1} p{print} p&&$0=="}"{exit}' \
    "$entrypoint" >> "$funcs"
  if ! grep -q "^$fn() {" "$funcs"; then
    echo "FAIL: could not extract $fn from $entrypoint"; exit 1
  fi
done
# shellcheck disable=SC1090
. "$funcs"

# The set of config-dir names the entrypoint links by default, read straight from
# entrypoint.sh so a change to the shared XDG paths is exercised automatically.
default_dirs="$(sed -n \
  's/^AGENT_CONFIG_DIRS="\${AGENT_CONFIG_DIRS:-\(.*\)}"$/\1/p' "$entrypoint")"
if [ -z "$default_dirs" ]; then
  echo "FAIL: could not read default AGENT_CONFIG_DIRS from $entrypoint"; exit 1
fi

# --- chown stub: record (recursive, owner, group, path) for each target it is
# asked to change. Real chown needs root to change owner across uids; the model
# is what lets this run unprivileged. The group is tracked because who-can-write
# on a 0775 dir turns on it, not just the owner.
CHOWN_LOG="$(mktemp)"
trap 'rm -f "$funcs" "$CHOWN_LOG"' EXIT
chown() {
  local recursive=0 own="" grp="" first=1 a
  for a in "$@"; do
    case "$a" in
      -R|-*R*) recursive=1; continue ;;
      -h|-*)   continue ;;
    esac
    if [ "$first" = 1 ]; then
      own="${a%%:*}"
      case "$a" in *:*) grp="${a#*:}" ;; *) grp="" ;; esac
      first=0; continue
    fi
    printf '%s\t%s\t%s\t%s\n' "$recursive" "$own" "$grp" "$a" >> "$CHOWN_LOG"
  done
}

# Effective owner:group of a directory = set by the last chown that covers it
# (directly, or recursively via an ancestor), else "root:root" (the uid/gid that
# runs the entrypoint and created it via mkdir -p). Printed as "owner:group".
effective_ownership() {
  local d="$1" own="root" grp="root" rec o g p
  while IFS=$'\t' read -r rec o g p; do
    if [ "$p" = "$d" ] || { [ "$rec" = 1 ] && [ "${d#"$p"/}" != "$d" ]; }; then
      own="$o"; grp="$g"
    fi
  done < "$CHOWN_LOG"
  printf '%s:%s' "$own" "$grp"
}

# Assert every directory between $HOME (exclusive) and the link's parent
# (inclusive) is owned by ILD's user and group, so ILD can write it.
assert_chain_owned() {
  local home="$1" name="$2" cur rel og eo eg
  cur="$(dirname "$home/$name")"
  while [ "$cur" != "$home" ] && [ "$cur" != "/" ]; do
    rel="${cur#"$home"/}"
    og="$(effective_ownership "$cur")"
    eo="${og%%:*}"
    eg="${og#*:}"
    if [ "$eo" != "$owner" ]; then
      fail "$name: \$HOME/$rel left owned by '$eo' (need '$owner') -> install's Directory.CreateDirectory(\$HOME/.local/bin) denied"
    elif [ "$eg" != "$group" ]; then
      fail "$name: $HOME/$rel is group '$eg' (expected '$group')"
    else
      pass "$name: \$HOME/$rel is $eo:$eg (writable by ILD)"
    fi
    cur="$(dirname "$cur")"
  done
}

# --- Run the real helper against a scratch home, then check the invariant for
# every nested (contains '/') config-dir name.
home="$(mktemp -d)"
store="$(mktemp -d)"
trap 'rm -f "$funcs" "$CHOWN_LOG"; rm -rf "$home" "$store"' EXIT

# shellcheck disable=SC2086
link_agent_config_dirs "$store" "$home" "$owner" "$group" $default_dirs

checked_nested=0
for name in $default_dirs; do
  case "$name" in
    */*) checked_nested=1; assert_chain_owned "$home" "$name" ;;
  esac
done
if [ "$checked_nested" = 0 ]; then
  echo "FAIL: no nested AGENT_CONFIG_DIRS entry to exercise the intermediate chain"
  exit 1
fi

if [ "$failures" -ne 0 ]; then
  echo "$failures assertion(s) failed"; exit 1
fi
echo "all entrypoint link-store tests passed"
