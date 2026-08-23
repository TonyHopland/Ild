#!/usr/bin/env bash
#
# Regression test for per-deployment credentials in the compose stack (WI-108).
#
# Why this needs a test at all: a credential that is the same on every install is
# a credential every install's attacker already knows, and reintroducing one has
# NO runtime symptom — the stack comes up and works perfectly. It used to ship
# with `postgres_password` / `ild_core_password` / `ild_workitems_password` baked
# into docker-compose.yml, the database init script and the docs, which meant the
# lower-trust agent uid (ADR-0014) could reach PostgreSQL on the compose network
# with credentials it could read out of this repository. The three assertions
# below are what stops any of that coming back:
#
#   1. None of the retired constants appears anywhere in the tracked tree.
#   2. Every secret the stack has no default for is declared `${VAR:?...}`, so
#      `docker compose up` fails fast instead of booting on a shared value.
#   3. The `postgres` service publishes no host port, so the database is reachable
#      only from the compose network.
#
# (2) and (3) are read out of `docker compose config` where the docker CLI is
# available — compose's own resolved view — and out of the raw file otherwise;
# that degradation is announced, never silent. (1) is a tree-wide grep either way.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
compose_file="$repo_root/docker-compose.yml"

retired_constants="postgres_password ild_core_password ild_workitems_password"
required_vars="POSTGRES_PASSWORD ILD_DB_PASSWORD WORKITEM_DB_PASSWORD ILD_SESSION_TOKEN_PEPPER WORKITEM_API_KEYS"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

# --- 1. No shipped credential constant survives anywhere. --------------------
# Tracked files only: a developer's own .env is theirs and is gitignored. This
# script excludes itself, since it necessarily names the strings it forbids.
self="${0#"$repo_root"/}"
for constant in $retired_constants; do
  hits="$(cd "$repo_root" && git grep -In -F -e "$constant" -- . ":(exclude)$self" 2>/dev/null)"
  if [ -n "$hits" ]; then
    fail "the retired shared credential '$constant' is still in the tree:"
    printf '%s\n' "$hits" | sed 's/^/      /'
  else
    pass "no occurrence of the retired shared credential '$constant'"
  fi
done

# --- Resolve the compose model for the remaining assertions. -----------------
# Dummies for the fail-if-unset variables: their values are irrelevant here, they
# only have to let interpolation complete so compose can emit a model at all.
model="$compose_file"
resolved=""
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  resolved="$(mktemp)"
  compose_err="$(mktemp)"
  trap 'rm -f "$resolved" "$compose_err"' EXIT
  if ! (cd "$repo_root" \
        && POSTGRES_PASSWORD=ci-dummy ILD_DB_PASSWORD=ci-dummy \
           WORKITEM_DB_PASSWORD=ci-dummy ILD_SESSION_TOKEN_PEPPER=ci-dummy \
           WORKITEM_API_KEYS=ci-dummy ILD_PASSWORD=ci-dummy \
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

# --- 2. Every credential with no default fails the stack fast when unset. -----
# Asserted against the raw file: `${VAR:?...}` is an interpolation directive that
# `docker compose config` has already consumed, so only the source shows it.
for var in $required_vars; do
  if grep -q "\${$var:?" "$compose_file"; then
    pass "compose refuses to start without $var"
  else
    fail "$var is not declared '\${$var:?...}' in docker-compose.yml, so an unset" \
         "or empty value would boot the stack on whatever default is left behind"
  fi
done

# --- 3. PostgreSQL is not published to the host. -----------------------------
# Reads the whole `postgres` service block and looks for any port mapping in it,
# in either dialect: `published:` in the resolved model, `- "5432:5432"` in the
# raw file. Commented-out lines do not count - that is the supported opt-in.
postgres_block="$(awk '
  /^  postgres:$/ { inside = 1; next }
  /^  [a-zA-Z_-]+:$/ { inside = 0 }
  inside && $0 !~ /^[[:space:]]*#/ { print }
' "$model")"

if [ -z "$postgres_block" ]; then
  fail "could not read the 'postgres' service block out of the compose model"
elif printf '%s\n' "$postgres_block" | grep -Eq '^[[:space:]]*(- +")?[0-9]+:[0-9]+"?[[:space:]]*$|published:'; then
  fail "the 'postgres' service publishes a host port. The database is reachable" \
       "from the agent uid on the compose network already; publishing it widens" \
       "that to the host. Leave the mapping commented out as an opt-in."
else
  pass "the 'postgres' service publishes no host port"
fi

if [ "$failures" -ne 0 ]; then
  echo "$failures assertion(s) failed"; exit 1
fi
echo "all compose credential tests passed"
