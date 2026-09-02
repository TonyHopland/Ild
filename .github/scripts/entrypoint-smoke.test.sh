#!/usr/bin/env bash
#
# Smoke test: entrypoint.sh must run end to end as an unprivileged user.
#
# The other entrypoint tests slice individual functions out of the file and never
# execute its top-level body, and `sh -n` only parses. Neither noticed when an
# editing slip left a copy of a function body ABOVE the shebang: every function
# test passed, the syntax check passed, and the container died on line 3 before
# doing anything at all. This runs the real script the way the Dockerfile does
# (`/bin/sh /entrypoint.sh <cmd>`), as whoever runs the tests, with no database
# configured — which is the non-root branch: no setup, no wait, just `exec "$@"`.
# If the command it is handed runs, the script's top level is sound.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
entrypoint="$repo_root/entrypoint.sh"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

if [ "$(id -u)" -eq 0 ]; then
  echo "This test must run unprivileged (it exercises the non-root exec path); skipping the root branch."
fi

if head -1 "$entrypoint" | grep -q '^#!/bin/sh$'; then
  pass "the shebang is the first line"
else
  fail "the first line of entrypoint.sh is not the shebang: $(head -1 "$entrypoint")"
fi

# Everything the script might otherwise wait on or branch on is cleared, so the
# run is deterministic and fast whatever the host has in its environment.
marker="entrypoint-reached-$$"
output="$(env -u ILD_DB_CONNECTION_STRING -u WORKITEM_DB_CONNECTION_STRING -u AGENT_USER \
  sh "$entrypoint" sh -c "echo $marker" 2>&1)"
status=$?

if [ "$status" -eq 0 ]; then
  pass "entrypoint.sh exits 0 when handed a trivial command"
else
  fail "entrypoint.sh exited $status: $output"
fi
if [ "$output" = "$marker" ]; then
  pass "the handed command ran, and nothing else was printed"
else
  fail "expected only '$marker' on output, got: $output"
fi

if [ "$failures" -ne 0 ]; then
  echo "$failures assertion(s) failed"; exit 1
fi
echo "all entrypoint smoke tests passed"
