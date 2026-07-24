#!/usr/bin/env bash
#
# Regression test for entrypoint.sh's config-store linking (WI-163).
#
# Bug: starting a work item fails at the Start node's install step with
#   ild.config install failed: Access to the path '/home/ild/.local/bin' is denied.
#
# Root cause is in link_agent_config_dirs. For a *nested* AGENT_CONFIG_DIRS entry
# such as `.local/share/opencode` it computes link_parent=$HOME/.local/share and
# runs `mkdir -p "$link_parent"` — which, as the root uid the entrypoint runs as,
# also creates the intermediate `$HOME/.local`. It then chowned only the immediate
# `$link_parent` to the orchestrator, leaving the intermediate `$HOME/.local`
# root-owned (0775 under umask 002). The orchestrator user `ild` is then only
# "other" on it (r-x, no write), so WorktreePreviewService.BuildDefaultEnvironment
# later fails to Directory.CreateDirectory("$HOME/.local/bin").
#
# The `.local/share/opencode` entry (added when opencode's XDG data dir was
# shared) is what first drove `.local` two levels deep; `.config/opencode` is
# only one level deep, so its parent IS the immediate parent and was chowned.
#
# The fix must also get the GROUP right, not just the owner. These 0775 dirs are
# the orchestrator's own home scaffolding; owning the chain with the *shared*
# agent group would let the agent uid (a member of that group) write $HOME/.local
# and shadow a tool the orchestrator later execs from $HOME/.local/bin — the
# cross-uid escalation ADR-0014 exists to prevent. So the chain must be owned by
# the orchestrator with its *private* group, never the shared one.
#
# This test sources the real link helper, records the owner AND group of every
# path `chown` touches, and asserts the FULL intermediate chain of each nested
# link — not just the immediate parent — ends up (a) owner = orchestrator and
# (b) group = the orchestrator-private group, i.e. NOT the shared agent group.
# It fails both if the chain is left root-owned (the original bug) and if it is
# owned with the shared group (the write-widening the first fix introduced),
# honoring docs/adr/0014-agent-uid-isolation.md.
#
# Runs unprivileged (no root / no real second uid): the chown stub models
# ownership so the invariant is checked purely from what the script asks for.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
entrypoint="$repo_root/entrypoint.sh"

owner="ild"            # the orchestrator uid the install step runs as
group="ild-agents"     # the shared group under uid isolation (the agent is in it)
home_group="ild"       # the orchestrator's PRIVATE group; the parent chain must
                       # use this, never $group, or the agent gains write on it

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

# --- Load the real link helper without running the entrypoint's top-level body.
# link_agent_config_dirs delegates the parent-chain chown to ensure_home_link_parent
# (the shared helper that carries the fix), so both are sourced — the test still
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
# (inclusive) is owned by the orchestrator (so it is writable by it) AND carries
# the orchestrator-private group, NOT the shared agent group (so a 0775 mode does
# not hand the agent uid write on the orchestrator's home scaffolding).
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
    elif [ "$eg" = "$group" ]; then
      fail "$name: \$HOME/$rel is group '$eg' (the shared agent group) -> under umask 002 (0775) the agent uid gains WRITE and could shadow a tool the orchestrator execs from \$HOME/.local/bin (ADR-0014)"
    elif [ "$eg" != "$home_group" ]; then
      fail "$name: \$HOME/$rel is group '$eg' (expected orchestrator-private '$home_group')"
    else
      pass "$name: \$HOME/$rel is $eo:$eg (orchestrator-writable, agent has no group write)"
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
link_agent_config_dirs "$store" "$home" "$owner" "$group" "$home_group" $default_dirs

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
