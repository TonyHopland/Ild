#!/usr/bin/env bash
#
# Tests for entrypoint.sh's grant_engine_socket.
#
# The container run host drives the engine through the socket at ILD_CONTAINER_SOCKET, and whoever
# can open it controls the engine. The entrypoint gives the orchestrator user (only) a group that can
# open it, since the drop to that user rebuilds its groups from /etc/group and a compose group_add is lost.
#
# Sources the real functions. stat, getent, groupadd, usermod, chgrp and id are stubbed to answer from
# files in a scratch directory and record what was changed, so this runs unprivileged.
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
for name in in_user_namespace grant_engine_socket; do
  awk -v hdr="$name() {" '$0==hdr{p=1} p{print} p&&$0=="}"{exit}' "$entrypoint" >> "$funcs"
  grep -q "^$name() {" "$funcs" || { echo "FAIL: could not extract $name from $entrypoint"; exit 1; }
done

socket="$work/engine.sock"
if command -v python3 >/dev/null 2>&1; then
  python3 -c 'import socket,sys; s=socket.socket(socket.AF_UNIX); s.bind(sys.argv[1])' "$socket"
else
  perl -MIO::Socket::UNIX -e 'IO::Socket::UNIX->new(Type => SOCK_STREAM(), Local => $ARGV[0], Listen => 1) or die' "$socket"
fi
[ -S "$socket" ] || { echo "FAIL: could not make a socket"; exit 1; }
printf '0 0 4294967295\n' > "$work/uid_map.host"
printf '0 1000 1\n1 524288 65536\n' > "$work/uid_map.userns"

# run <socket gid> <socket mode> <uid map> <group db> <agent gids> [ILD_RUN_HOST] [socket path]
run() {
  : > "$work/calls"
  printf '%s' "$4" > "$work/group"
  (
    stat() { case "$2" in %g) echo "$sock_gid" ;; %A) echo "$sock_mode" ;; esac; }
    # Like getent: exit 2 when nothing matches.
    getent() { awk -F: -v k="$2" '$1==k || $3==k {print; found=1} END {exit found ? 0 : 2}' "$work/group"; }
    groupadd() {
      echo "groupadd $*" >> "$work/calls"
      if [ "$1" = "--gid" ]; then echo "$3:x:$2:" >> "$work/group"; else echo "$1:x:5555:" >> "$work/group"; fi
    }
    usermod() { echo "usermod $*" >> "$work/calls"; }
    chgrp() { echo "chgrp $1" >> "$work/calls"; }
    id() { echo "$agent_gids"; }
    sock_gid="$1" sock_mode="$2" agent_gids="$5"
    UID_MAP_FILE="$3" ILD_RUN_HOST="${6-container}" ILD_CONTAINER_SOCKET="${7-unix://$socket}"
    RUNTIME_USER=ild AGENT_USER=agent
    # shellcheck disable=SC1090
    . "$funcs"
    grant_engine_socket
  ) > "$work/stdout" 2> "$work/stderr"
}

calls() { tr '\n' ';' < "$work/calls"; }
groups='ild:x:10001:
ild-agents:x:10003:ild,agent
agent:x:10002:
'

run 0 srw-rw---- "$work/uid_map.userns" "$groups" "10002 10003" "local"
[ -z "$(calls)" ] && pass "another run host: nothing is touched" || fail "another run host touched: $(calls)"

run 0 srw-rw---- "$work/uid_map.userns" "$groups" "10002 10003" container "/nowhere.sock"
if [ -z "$(calls)" ] && grep -q "is not a socket" "$work/stderr"; then pass "a missing socket is said, nothing is touched"; else fail "missing socket: $(calls) / $(cat "$work/stderr")"; fi

run 0 srw-rw---- "$work/uid_map.userns" "$groups" "10002 10003"
if [ "$(calls)" = "groupadd ild-engine;chgrp ild-engine;usermod -aG ild-engine ild;" ]; then
  pass "rootless (root:root in a user namespace): the socket goes to ild-engine and only ild joins it"
else fail "rootless: $(calls)"; fi

run 0 srw-rw---- "$work/uid_map.userns" "${groups}ild-engine:x:5555:ild
" "10002 10003"
if [ "$(calls)" = "chgrp ild-engine;usermod -aG ild-engine ild;" ]; then pass "a restart reuses ild-engine"; else fail "restart: $(calls)"; fi

run 0 srw-rw---- "$work/uid_map.host" "$groups" "10002 10003"
if [ -z "$(calls)" ] && grep -q "host's root group" "$work/stderr"; then
  pass "the host's root group outside a user namespace is left alone and said"
else fail "rootful root group: $(calls) / $(cat "$work/stderr")"; fi

run 999 srw-rw---- "$work/uid_map.host" "${groups}docker:x:999:
" "10002 10003"
if [ "$(calls)" = "usermod -aG docker ild;" ]; then pass "a socket of the docker group: ild joins docker"; else fail "docker group: $(calls)"; fi

run 998 srw-rw---- "$work/uid_map.host" "$groups" "10002 10003"
if [ "$(calls)" = "groupadd --gid 998 ild-engine-998;usermod -aG ild-engine-998 ild;" ]; then
  pass "a socket gid with no group here: a group of that gid is made and ild joins it"
else fail "unknown gid: $(calls)"; fi

run 10003 srw-rw---- "$work/uid_map.host" "$groups" "10002 10003"
if ! grep -q usermod "$work/calls" && grep -q "would hand the agent the engine" "$work/stderr"; then
  pass "a group the agent is in is never joined"
else fail "agent's group: $(calls) / $(cat "$work/stderr")"; fi

run 999 srw-r----- "$work/uid_map.host" "${groups}docker:x:999:
" "10002 10003"
if grep -q "not readable and writable by its group" "$work/stderr"; then pass "a socket its group cannot write is said"; else fail "mode: $(cat "$work/stderr")"; fi

echo
if [ "$failures" -gt 0 ]; then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
