#!/usr/bin/env bash
#
# The agent uid never gets the provider logins (docs/adr/0031-model-gateway.md).
# Sources the real seed_agent_config and checks that the agent's home gets its own
# copy of the CLI state without any credential file, that a link into the store left
# by an earlier version is replaced, and that a copy already in the agent's home is
# kept. Runs unprivileged: chown is stubbed.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
entrypoint="$(cd "$here/../.." && pwd)/entrypoint.sh"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

funcs="$(mktemp)"
store="$(mktemp -d)"
home="$(mktemp -d)"
trap 'rm -rf "$funcs" "$store" "$home"' EXIT

for fn in ensure_home_link_parent seed_agent_config ensure_credential_store; do
  awk -v hdr="$fn() {" '$0==hdr{p=1} p{print} p&&$0=="}"{exit}' "$entrypoint" >> "$funcs"
  grep -q "^$fn() {" "$funcs" || { echo "FAIL: could not extract $fn"; exit 1; }
done
# shellcheck disable=SC1090
. "$funcs"
chown() { :; }

AGENT_CREDENTIAL_FILES="$(sed -n 's/^AGENT_CREDENTIAL_FILES="\${AGENT_CREDENTIAL_FILES:-\(.*\)}"$/\1/p' "$entrypoint")"
dirs="$(sed -n 's/^AGENT_CONFIG_DIRS="\${AGENT_CONFIG_DIRS:-\(.*\)}"$/\1/p' "$entrypoint")"
files="$(sed -n 's/^AGENT_CONFIG_FILES="\${AGENT_CONFIG_FILES:-\(.*\)}"$/\1/p' "$entrypoint")"
[ -n "$AGENT_CREDENTIAL_FILES" ] && [ -n "$dirs" ] || { echo "FAIL: defaults not found in entrypoint.sh"; exit 1; }

put() { mkdir -p "$store/$(dirname "$1")"; echo "$2" > "$store/$1"; }
put .claude/.credentials.json secret
put .claude/projects/p/session.jsonl history
put .claude/settings.json settings
put .claude.json state
put .codex/auth.json secret
put .codex/config.toml config
put .codex/sessions/2026/rollout.jsonl history
put .copilot/config.json secret
put .copilot/settings.json settings
put .pi/agent/auth.json secret
put .pi/agent/settings.json settings
put .local/share/opencode/auth.json secret
put .local/share/opencode/state.json state

ln -s "$store/.opencode" "$home/.codex" 2>/dev/null
mkdir -p "$home/.pi/agent" && echo mine > "$home/.pi/agent/settings.json"

# shellcheck disable=SC2086
seed_agent_config "$home" agent "$store" $dirs $files

for kept in .claude/projects/p/session.jsonl .claude/settings.json .claude.json .codex/config.toml \
            .codex/sessions/2026/rollout.jsonl .copilot/settings.json .local/share/opencode/state.json; do
  if [ -f "$home/$kept" ]; then pass "$kept is copied"; else fail "$kept was not copied"; fi
done
for secret in .claude/.credentials.json .codex/auth.json .copilot/config.json .pi/agent/auth.json .local/share/opencode/auth.json; do
  if [ -e "$home/$secret" ]; then fail "$secret reached the agent's home"; else pass "$secret stays in the store"; fi
done
if [ -L "$home/.codex" ]; then fail ".codex is still a link"; else pass ".codex link from an earlier version is replaced"; fi
if [ "$(cat "$home/.pi/agent/settings.json")" = mine ]; then pass "an existing copy is kept"; else fail "an existing copy was overwritten"; fi
if grep -rq secret "$home"; then fail "a credential's content is somewhere in the agent's home"; else pass "no credential content in the agent's home"; fi

# The store itself: owner only, whatever the group and ACL grants of an earlier version left in it.
RUNTIME_USER="$(id -un)"
RUNTIME_GROUP="$(id -gn)"
umask 002
shared="$(mktemp -d)"
trap 'rm -rf "$funcs" "$store" "$home" "$shared"' EXIT
mkdir -p "$shared/.claude/projects"
echo secret > "$shared/.claude/.credentials.json"
chmod 2775 "$shared" "$shared/.claude" "$shared/.claude/projects"
chmod 0664 "$shared/.claude/.credentials.json"
ensure_credential_store "$shared"
if [ "$(stat -c %a "$shared")" = 700 ]; then pass "the store is 0700"; else fail "the store is $(stat -c %a "$shared")"; fi
if find "$shared" -perm /g+rwxs,o+rwx -print -quit | grep -q .; then fail "group or other access is left in the store"; else pass "no group or other access is left in the store"; fi

[ "$failures" -eq 0 ] || { echo "$failures assertion(s) failed"; exit 1; }
echo "all entrypoint credential-store tests passed"
