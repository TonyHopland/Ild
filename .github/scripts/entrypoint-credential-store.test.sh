#!/usr/bin/env bash
#
# The provider logins are ILD's alone (docs/adr/0031-model-gateway.md): the store is
# owner-only, whatever the group and ACL grants of an earlier version, whose agents
# ran as a second uid, left in it. Sources the real ensure_credential_store.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
entrypoint="$(cd "$here/../.." && pwd)/entrypoint.sh"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

funcs="$(mktemp)"
shared="$(mktemp -d)"
trap 'rm -rf "$funcs" "$shared"' EXIT

awk -v hdr="ensure_credential_store() {" '$0==hdr{p=1} p{print} p&&$0=="}"{exit}' "$entrypoint" > "$funcs"
grep -q "^ensure_credential_store() {" "$funcs" || { echo "FAIL: could not extract ensure_credential_store"; exit 1; }
# shellcheck disable=SC1090
. "$funcs"

RUNTIME_USER="$(id -un)"
RUNTIME_GROUP="$(id -gn)"
umask 002
mkdir -p "$shared/.claude/projects"
echo secret > "$shared/.claude/.credentials.json"
chmod 2775 "$shared" "$shared/.claude" "$shared/.claude/projects"
chmod 0664 "$shared/.claude/.credentials.json"
ensure_credential_store "$shared"
if [ "$(stat -c %a "$shared")" = 700 ]; then pass "the store is 0700"; else fail "the store is $(stat -c %a "$shared")"; fi
if find "$shared" -perm /g+rwxs,o+rwx -print -quit | grep -q .; then fail "group or other access is left in the store"; else pass "no group or other access is left in the store"; fi

[ "$failures" -eq 0 ] || { echo "$failures assertion(s) failed"; exit 1; }
echo "all entrypoint credential-store tests passed"
