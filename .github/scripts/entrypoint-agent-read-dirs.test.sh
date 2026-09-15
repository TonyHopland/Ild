#!/usr/bin/env bash
#
# Tests for entrypoint.sh's ensure_agent_read_dirs.
#
# AGENT_READ_DIR holds the files carrying the ILD API token (pi's ILD extension,
# the agent CLIs' MCP configs). The entrypoint provisions it and the app's two
# fixed folders in it, ild-pi-ext and ild-mcp-config, with ensure_shared_ro, which
# runs as root and would chown and chmod whatever a symlink there points at. So a
# symlink at any of the three must stop the entrypoint with a clear message before
# ensure_shared_ro runs at all.
#
# Sources the real function; ensure_shared_ro is stubbed to record its calls, so
# this runs unprivileged.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
entrypoint="$repo_root/entrypoint.sh"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

funcs="$work/funcs.sh"
awk -v hdr="ensure_agent_read_dirs() {" '$0==hdr{p=1} p{print} p&&$0=="}"{exit}' "$entrypoint" > "$funcs"
if ! grep -q "^ensure_agent_read_dirs() {" "$funcs"; then
  echo "FAIL: could not extract ensure_agent_read_dirs from $entrypoint"; exit 1
fi

# Runs the function in a subshell (it exits on refusal) with ensure_shared_ro
# stubbed; prints its stderr, leaves the calls in $work/calls.
run() {
  : > "$work/calls"
  (
    ensure_shared_ro() { echo "$1" >> "$work/calls"; }
    # shellcheck disable=SC1090
    . "$funcs"
    ensure_agent_read_dirs "$1"
  ) 2> "$work/stderr"
}

# --- Real directories: all three provisioned, root first.
root="$work/clean/read"
mkdir -p "$root/ild-pi-ext"
if run "$root"; then
  expected="$(printf '%s\n' "$root" "$root/ild-pi-ext" "$root/ild-mcp-config")"
  if [ "$(cat "$work/calls")" = "$expected" ]; then
    pass "real directories are provisioned, root first"
  else
    fail "unexpected ensure_shared_ro calls: $(cat "$work/calls")"
  fi
else
  fail "refused real directories: $(cat "$work/stderr")"
fi

# --- A symlink at each of the three: refused, with a message, before anything is provisioned.
for linked in "" "/ild-pi-ext" "/ild-mcp-config"; do
  case_dir="$work/link${linked//\//-}"
  root="$case_dir/read"
  target="$case_dir/elsewhere"
  mkdir -p "$target"
  if [ -z "$linked" ]; then
    ln -s "$target" "$root"
  else
    mkdir -p "$root"
    ln -s "$target" "$root$linked"
  fi

  if run "$root"; then
    fail "a symlink at $root$linked was accepted"
  elif ! grep -q "$root$linked is a symlink" "$work/stderr"; then
    fail "no clear message for a symlink at $root$linked: $(cat "$work/stderr")"
  elif [ -s "$work/calls" ]; then
    fail "ensure_shared_ro ran despite a symlink at $root$linked: $(cat "$work/calls")"
  else
    pass "a symlink at AGENT_READ_DIR${linked} stops the entrypoint before ensure_shared_ro"
  fi
done

if [ "$failures" -ne 0 ]; then
  echo "$failures assertion(s) failed"; exit 1
fi
echo "all entrypoint agent-read-dir tests passed"
