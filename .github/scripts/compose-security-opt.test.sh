#!/usr/bin/env bash
#
# Regression test for `no-new-privileges` on the compose services (WI-159).
#
# Why this needs a test at all: a missing `security_opt:` line has NO runtime
# symptom. The stack builds, boots, and behaves identically without it, so a
# future compose refactor could drop it and every other check in this repo would
# stay green. The flag is what stops a process in these containers from gaining
# privilege on execve through a setuid/setgid bit or a file capability. That
# matters most in the `ild` image, which both carries setuid-root binaries
# (util-linux's mount/su, Chrome's chrome-sandbox) and runs the lower-trust agent
# uid (ADR-0014) they would be an escalation route for. Nothing in either image
# needs the gain the flag refuses — the orchestrator drops privilege retaining
# capabilities it already holds and spawns the agent through `setpriv` rather
# than a setuid helper, the WorkItem server drops with `gosu`, and every Chrome
# launch path passes --no-sandbox — so the flag costs nothing to keep and would
# cost nothing visible to lose.
#
# The assertion is deliberately PER SERVICE. A file-wide grep for the string
# would keep passing after the flag was moved to, or left on, only one of them —
# it would assert that the file mentions the flag, not that these two containers
# run with it. `postgres` is intentionally not asserted: it is a third-party
# image whose entrypoint we do not control across version bumps.
#
# Input: `docker compose config` when the docker CLI is available — compose's
# own resolved view of the file, so the assertion cannot be fooled by YAML
# structure that the scan below reads differently than compose does. That needs
# no daemon (it is pure variable interpolation) but does need values for the
# file's fail-if-unset variables, supplied here as dummies. Where the CLI is
# absent (sandboxes, minimal images) the raw compose file is scanned instead;
# that degradation is announced in the output, never silent. Either way the
# assertions run — the CLI only improves the input they run against.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
compose_file="$repo_root/docker-compose.yml"

required_flag="no-new-privileges:true"
required_services="ild workitem-server"

# The one capability the `ild` entrypoint spends before dropping privilege: the
# uid-keyed firewall rules that make the agent egress proxy an enforced boundary
# rather than an advisory one (ADR-0019). Its absence has no runtime symptom
# either — the stack boots and only Settings says "advisory".
required_cap="NET_ADMIN"
cap_services="ild"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

tmpfiles=""
# shellcheck disable=SC2086
cleanup() { [ -n "$tmpfiles" ] && rm -f $tmpfiles; }
trap cleanup EXIT
mktmp() { local f; f="$(mktemp)"; tmpfiles="$tmpfiles $f"; printf '%s' "$f"; }

# --- Read one key's values out of one service's block. -----------------------
# Tracks the top-level `services:` mapping and the indentation of the wanted
# service's block, so a match under a *different* service, deeper inside this
# one, or under some other top-level key can never satisfy an assertion. Prints
# one value per line; prints nothing when the service or the key is absent.
# Handles the three list forms YAML allows here — block sequence indented under
# its key, block sequence at the key's own indent, and the inline flow form —
# because the raw compose file may legally use any of them. (`docker compose
# config` normalises them all to the first, so the other two matter only on the
# raw-file path, which is exactly the path CI never exercises.)
extract_awk=$(cat <<'AWK'
function trim(s) { sub(/^[ \t]+/, "", s); sub(/[ \t]+$/, "", s); return s }
function unquote(s,   q) {
  s = trim(s)
  q = substr(s, 1, 1)
  if ((q == "\"" || q == "'") && length(s) > 1 && substr(s, length(s)) == q)
    s = substr(s, 2, length(s) - 2)
  return s
}
function emit_flow(s,   n, i, parts) {
  sub(/^\[/, "", s); sub(/\]$/, "", s)
  n = split(s, parts, ",")
  for (i = 1; i <= n; i++) if (trim(parts[i]) != "") print unquote(parts[i])
}
{ text = trim($0) }
text == "" || text ~ /^#/ { next }
{ match($0, /^ */); ind = RLENGTH }
ind == 0 {
  in_services = (text == "services:")
  in_svc = 0; in_list = 0; svc_ind = -1; key_ind = -1
  next
}
!in_services { next }
{ if (svc_ind < 0) svc_ind = ind }
ind == svc_ind {
  in_svc = (text == want ":")
  in_list = 0; key_ind = -1
  next
}
!in_svc { next }
{ if (key_ind < 0) key_ind = ind }
# A block sequence may sit at its key's own indent or deeper, so both continue
# the list. A sibling key at key_ind fails the `- ` test and closes it; anything
# at the service or top level was already consumed by the rules above.
in_list {
  if (ind >= key_ind && text ~ /^- /) { print unquote(substr(text, 3)); next }
  in_list = 0
}
ind == key_ind && index(text, key ":") == 1 {
  rest = trim(substr(text, length(key) + 2))
  if (rest == "") { in_list = 1 }
  else if (index(rest, "[") == 1) { emit_flow(rest) }
  else { print unquote(rest) }
}
AWK
)

values_in() { # <file> <service> <key> -> one value per line
  awk -v want="$2" -v key="$3" "$extract_awk" "$1"
}

# Every variable `docker compose config` would refuse to interpolate: the
# `${VAR:?...}` required ones, plus bare `${VAR}` references. Printed as
# NAME=ci-dummy assignments, one per line.
compose_dummy_env() { # <compose file>
  {
    grep -o '\${[A-Za-z_][A-Za-z0-9_]*:?' "$1" | sed 's/^\${//; s/:?$//'
    grep -o '\${[A-Za-z_][A-Za-z0-9_]*}' "$1" | sed 's/^\${//; s/}$//'
  } | sort -u | sed 's/$/=ci-dummy/'
}

# --- Self-test: pin the scanner against fixtures before trusting it. ---------
# The scanner is the only part of this test that can be subtly wrong — the two
# assertions built on it are trivial — so it is exercised against inputs written
# to break it rather than against values scraped from the file it is asserting
# on. Each case is an exact-equality read, because the failure mode that matters
# is not only under-capture (the flag reported missing while it sits in the
# file) but over-capture: a list that never closes would swallow every following
# key's items, and the `grep -Fxq` assertion below would still find the flag
# among them and pass. Reading real values out of docker-compose.yml as a smoke
# check would also couple this security test to unrelated services' settings.
#
# Two documents, because the scanner has two dialects to survive: hand-written
# compose YAML (every list form the file may legally be reformatted into) and
# the shape `docker compose config` emits — which is the only input CI ever
# asserts against, and which no amount of exercising the raw file would pin.
selftest() {
  local fixture got want label

  # --- Dialect 1: hand-written compose YAML, all list forms plus decoys.
  fixture="$(mktmp)"
  cat > "$fixture" <<'YAML'
services:
  alpha:
    image: example
    security_opt:
      - "no-new-privileges:true"
      - label:disable
  beta:
    image: example
    security_opt:
    - no-new-privileges:true
  gamma:
    image: example
    security_opt: ["no-new-privileges:true", label:disable]
  delta:
    image: example
    labels:
      security_opt: "nested decoy, not this service's setting"
  epsilon:
    image: example
    security_opt:
      - "no-new-privileges:true"
    ports:
      - "8080:8080"
      - "3100:3100"
    volumes:
      - data:/data
  zeta:
    image: example
    security_opt:
    - no-new-privileges:true
    ports:
    - "8080:8080"
    - "3100:3100"
volumes:
  security_opt: top-level decoy, not a service at all
YAML

  expect() { # <label> <expected, space-joined> <service> <key>
    got="$(values_in "$fixture" "$3" "$4" | tr '\n' ' ')"
    got="${got%"${got##*[! ]}"}"
    if [ "$got" = "$2" ]; then pass "scanner: $1"
    else fail "scanner: $1 - read [$got], expected [$2]"; fi
  }

  expect "block list indented under its key" \
    "$required_flag label:disable" alpha security_opt
  expect "block list at its key's own indent" \
    "$required_flag" beta security_opt
  expect "inline flow list" \
    "$required_flag label:disable" gamma security_opt
  # The scoping property the per-service assertion rests on: four siblings set
  # the flag and `delta` does not, so a leaky scan shows up here as a value.
  expect "sibling services' flags do not leak into a service that has none" \
    "" delta security_opt
  # ...and `delta` is genuinely being read, so the empty result above means
  # "no such key", not "no such service".
  expect "the service with no security_opt is still found" \
    "example" delta image
  # Termination. Accepting a block sequence at its key's own indent (not just
  # deeper) is what makes the non-indented form work, and it is only safe if a
  # sibling key still closes the list — otherwise `security_opt` swallows the
  # `ports`/`volumes` items that follow it. `epsilon` and `zeta` are the only
  # services here whose `security_opt` is NOT their last key, so they are what
  # holds that: everywhere else the next line is a sibling *service*, which the
  # service-indent rule consumes before the list rule is reached.
  expect "list ends at the next key, indented form" \
    "$required_flag" epsilon security_opt
  expect "list ends at the next key, at-key-indent form" \
    "$required_flag" zeta security_opt

  # --- Dialect 2: the shape `docker compose config` emits. Differs from the
  # file in ways the scanner has to walk past: a leading top-level scalar,
  # services sorted with `security_opt` mid-block, block sequences of *mappings*
  # under neighbouring keys, and nested maps under `environment`/`depends_on`.
  # CI resolves the real file through this path, so this is the dialect the
  # merge-gating assertion actually runs against.
  fixture="$(mktmp)"
  cat > "$fixture" <<'YAML'
name: ild
services:
  ild:
    build:
      context: /repo
      dockerfile: Dockerfile
    container_name: ild
    depends_on:
      postgres:
        condition: service_healthy
        required: true
    environment:
      ASPNETCORE_URLS: http://+:8080
      ILD_DATA_PATH: /data
    networks:
      default: null
    ports:
      - mode: ingress
        target: 8080
        published: "8080"
        protocol: tcp
    restart: unless-stopped
    security_opt:
      - no-new-privileges:true
    volumes:
      - type: volume
        source: ild-data
        target: /data
        volume: {}
  postgres:
    image: postgres:17-alpine
    healthcheck:
      test:
        - CMD-SHELL
        - pg_isready -U postgres
    networks:
      default: null
    restart: unless-stopped
networks:
  default:
    name: ild_default
volumes:
  ild-data:
    name: ild_ild-data
YAML

  expect "compose-config dialect: flag read exactly, list ends at the next key" \
    "$required_flag" ild security_opt
  expect "compose-config dialect: a service without the flag reads empty" \
    "" postgres security_opt
  expect "compose-config dialect: that service is genuinely found" \
    "postgres:17-alpine" postgres image

  if [ "$failures" -ne 0 ]; then
    echo "The service-block scanner is broken, so every assertion below would be"
    echo "meaningless (a false pass as easily as a false fail). Fix the scanner."
    exit 1
  fi
}
selftest

# --- Resolve the compose model to assert against. ----------------------------
model="$compose_file"
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  resolved="$(mktmp)"
  compose_err="$(mktmp)"
  # Dummies for the `${VAR:?...}` fail-if-unset variables and the bare
  # `${ILD_PASSWORD}`; their values are irrelevant to what is asserted, they
  # only have to let interpolation complete. stderr is kept out of $resolved so
  # a future compose warning is reported rather than parsed as part of the model.
  #
  # The names are read out of the compose file rather than listed here. They are
  # setup, not an assertion, and a hand-written list goes stale the moment someone
  # adds a required variable to the compose file -- silently, because a machine
  # without the docker CLI takes the raw-file path below and never runs this.
  # shellcheck disable=SC2046  # word splitting is how the assignments are passed
  if ! (cd "$repo_root" \
        && env $(compose_dummy_env "$compose_file") \
           docker compose --file "$compose_file" config) \
       > "$resolved" 2>"$compose_err"; then
    echo "FAIL: 'docker compose config' could not resolve $compose_file:"
    cat "$compose_err"
    exit 1
  fi
  [ -s "$compose_err" ] && cat "$compose_err" >&2
  model="$resolved"
  echo "input: docker compose config (compose's own resolved view)"
else
  echo "input: $compose_file (raw file scan - docker CLI unavailable here)"
fi

# --- The assertion. ----------------------------------------------------------
for service in $required_services; do
  opts="$(values_in "$model" "$service" security_opt)"
  if [ -z "$opts" ]; then
    fail "service '$service' sets no security_opt at all; it needs '$required_flag'" \
         "(a setuid binary inside the container would otherwise still be an" \
         "escalation route - see the header of this script)"
  elif printf '%s\n' "$opts" | grep -Fxq "$required_flag"; then
    pass "service '$service' runs with '$required_flag'"
  else
    fail "service '$service' has security_opt [$(printf '%s' "$opts" | tr '\n' ' ')]" \
         "but not '$required_flag'"
  fi
done

for service in $cap_services; do
  caps="$(values_in "$model" "$service" cap_add)"
  if printf '%s\n' "$caps" | grep -Fxq "$required_cap"; then
    pass "service '$service' is granted '$required_cap' for the entrypoint's egress rules"
  else
    fail "service '$service' has cap_add [$(printf '%s' "$caps" | tr '\n' ' ')] but not '$required_cap'" \
         "(without it the agent egress filter runs in advisory mode - see ADR-0019)"
  fi
done

if [ "$failures" -ne 0 ]; then
  echo "$failures assertion(s) failed"; exit 1
fi
echo "all compose security_opt tests passed"
