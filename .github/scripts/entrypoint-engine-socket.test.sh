#!/usr/bin/env bash
#
# Tests for entrypoint.sh's grant_engine_socket.
#
# ILD in its image drives the engine through the socket at ILD_CONTAINER_SOCKET, and whoever can open it
# controls the engine. The entrypoint gives ILD's user a group that can open it, since the drop to that user
# rebuilds its groups from /etc/group and a compose group_add is lost.
#
# Sources the real functions. stat, getent, groupadd, usermod and chgrp are stubbed to answer from
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

# run <socket gid> <socket mode> <uid map> <group db> [ILD_IMAGE] [socket path] [KUBERNETES_SERVICE_HOST]
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
    sock_gid="$1" sock_mode="$2"
    UID_MAP_FILE="$3" ILD_IMAGE="${5-1}" ILD_CONTAINER_SOCKET="${6-unix://$socket}" KUBERNETES_SERVICE_HOST="${7-}"
    RUNTIME_USER=ild ILD_CONTAINER_SOCKET_GRANT="${grant-}"
    # shellcheck disable=SC1090
    . "$funcs"
    grant_engine_socket
  ) > "$work/stdout" 2> "$work/stderr"
}

calls() { tr '\n' ';' < "$work/calls"; }
groups='ild:x:10001:
'

run 0 srw-rw---- "$work/uid_map.userns" "$groups" ""
[ -z "$(calls)" ] && pass "not ILD's image (the WorkItem Server): nothing is touched" || fail "not ILD's image, touched: $(calls)"

run 0 srw-rw---- "$work/uid_map.userns" "$groups" 1 "unix://$socket" 10.0.0.1
[ -z "$(calls)" ] && pass "in Kubernetes (pods, no socket): nothing is touched" || fail "Kubernetes touched: $(calls)"

run 0 srw-rw---- "$work/uid_map.userns" "$groups" 1 "/nowhere.sock"
if [ -z "$(calls)" ] && grep -q "is not a socket" "$work/stderr"; then pass "a missing socket is said, nothing is touched"; else fail "missing socket: $(calls) / $(cat "$work/stderr")"; fi

run 0 srw-rw---- "$work/uid_map.userns" "$groups"
if [ "$(calls)" = "groupadd ild-engine;chgrp ild-engine;usermod -aG ild-engine ild;" ]; then
  pass "rootless (root:root in a user namespace): the socket goes to ild-engine and only ild joins it"
else fail "rootless: $(calls)"; fi

run 0 srw-rw---- "$work/uid_map.userns" "${groups}ild-engine:x:5555:ild
"
if [ "$(calls)" = "chgrp ild-engine;usermod -aG ild-engine ild;" ]; then pass "a restart reuses ild-engine"; else fail "restart: $(calls)"; fi

run 0 srw-rw---- "$work/uid_map.host" "$groups"
if [ -z "$(calls)" ] && grep -q "host's root group" "$work/stderr"; then
  pass "the host's root group outside a user namespace is left alone and said"
else fail "rootful root group: $(calls) / $(cat "$work/stderr")"; fi
if grep -q "ILD_CONTAINER_SOCKET_GRANT=1" "$work/stderr"; then pass "the refusal names the opt-in"; else fail "refusal: $(cat "$work/stderr")"; fi

grant=1 run 0 srw-rw---- "$work/uid_map.host" "$groups"
if [ "$(calls)" = "chgrp ild;usermod -aG ild ild;" ] && grep -q "root-equivalent" "$work/stdout"; then
  pass "rootful with ILD_CONTAINER_SOCKET_GRANT=1: the socket goes to ild's own group, and that is said"
else fail "rootful opt-in: $(calls) / $(cat "$work/stdout") / $(cat "$work/stderr")"; fi

grant=1 run 0 srw-rw---- "$work/uid_map.host" 'other:x:10002:
'
if [ -z "$(calls)" ] && grep -q "has no group ild" "$work/stderr"; then
  pass "rootful opt-in without ILD's group: left alone and said"
else fail "rootful opt-in, no group: $(calls) / $(cat "$work/stderr")"; fi

grant=1 run 0 srw-rw---- "$work/uid_map.userns" "$groups"
if [ "$(calls)" = "groupadd ild-engine;chgrp ild-engine;usermod -aG ild-engine ild;" ]; then
  pass "rootless with the opt-in set: still the private ild-engine group"
else fail "rootless with opt-in: $(calls)"; fi

run 999 srw-rw---- "$work/uid_map.host" "${groups}docker:x:999:
"
if [ "$(calls)" = "usermod -aG docker ild;" ]; then pass "a socket of the docker group: ild joins docker"; else fail "docker group: $(calls)"; fi

run 998 srw-rw---- "$work/uid_map.host" "$groups"
if [ "$(calls)" = "groupadd --gid 998 ild-engine-998;usermod -aG ild-engine-998 ild;" ]; then
  pass "a socket gid with no group here: a group of that gid is made and ild joins it"
else fail "unknown gid: $(calls)"; fi

run 999 srw-r----- "$work/uid_map.host" "${groups}docker:x:999:
"
if grep -q "not readable and writable by its group" "$work/stderr"; then pass "a socket its group cannot write is said"; else fail "mode: $(cat "$work/stderr")"; fi

echo
if [ "$failures" -gt 0 ]; then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
