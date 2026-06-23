#!/usr/bin/env bash
#
# Tests for compute-version.sh. Run as a CI step in ci.yml so the version
# stamping logic is verified before any publish job can use it.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/compute-version.sh"

failures=0

expect_ok() {
  local desc="$1" expected="$2"; shift 2
  local actual
  if ! actual="$("$script" "$@" 2>/dev/null)"; then
    echo "FAIL: $desc -> command exited non-zero"; failures=$((failures + 1)); return
  fi
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL: $desc -> expected '$expected', got '$actual'"; failures=$((failures + 1)); return
  fi
  echo "ok: $desc -> $actual"
}

expect_fail() {
  local desc="$1"; shift
  if "$script" "$@" >/dev/null 2>&1; then
    echo "FAIL: $desc -> expected non-zero exit, got success"; failures=$((failures + 1)); return
  fi
  echo "ok: $desc -> rejected as expected"
}

# Release tags strip the leading v and yield the bare version.
expect_ok "release tag v1.2.3"   "1.2.3"      tag v1.2.3
expect_ok "release tag v0.2.0"   "0.2.0"      tag v0.2.0
expect_ok "prerelease tag"       "2.0.0-rc.1" tag v2.0.0-rc.1

# Publishing is release-tag only: branch (main) builds no longer publish, so a
# branch ref is rejected outright. (The old pipeline stamped `<base>-main+<sha>`
# here; the trailing sha arg is now ignored, exercising that regression.)
expect_fail "branch ref is rejected (no main publishing)" branch main abc1234

# Edge cases: the stray bare tag and other non-canonical refs must be rejected.
expect_fail "bare tag 0.2.0 (no v prefix)" tag 0.2.0
expect_fail "non-semver tag"               tag vnope
expect_fail "unsupported ref type"         release v1.2.3

if [[ "$failures" -ne 0 ]]; then
  echo "$failures test(s) failed"; exit 1
fi
echo "all compute-version tests passed"
