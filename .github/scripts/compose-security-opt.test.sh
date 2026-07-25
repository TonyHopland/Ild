#!/usr/bin/env bash
#
# Regression test for `no-new-privileges` on the compose services (WI-159).
#
# Why this needs a test at all: a missing `security_opt:` line has NO runtime
# symptom. The stack builds, boots, and behaves identically without it, so a
# future compose refactor could drop it and every other check in this repo would
# stay green. The flag is what stops a process in these containers from gaining
# privilege on execve through a setuid/setgid bit or a file capability — an
# escalation route that matters most in the `ild` image, where the lower-trust
# agent uid (ADR-0014) runs alongside the orchestrator. Neither image relies on
# such a gain (the orchestrator drops via retained ambient capabilities and
# spawns the agent through `setpriv`; the WorkItem server drops via `gosu`), so
# the flag costs nothing to keep and would cost nothing visible to lose.
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

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

# --- Resolve the compose model to scan. -------------------------------------
resolved=""
cleanup() { [ -n "$resolved" ] && rm -f "$resolved"; }
trap cleanup EXIT

model="$compose_file"
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  resolved="$(mktemp)"
  # Dummies for the `${VAR:?...}` fail-if-unset variables and the bare
  # `${ILD_PASSWORD}`; their values are irrelevant to what is asserted, they
  # only have to let interpolation complete.
  if ! (cd "$repo_root" \
        && WORKITEM_API_KEYS=ci-dummy ILD_PASSWORD=ci-dummy \
           docker compose --file "$compose_file" config) > "$resolved" 2>&1; then
    echo "FAIL: 'docker compose config' could not resolve $compose_file:"
    cat "$resolved"
    exit 1
  fi
  model="$resolved"
  echo "input: docker compose config (compose's own resolved view)"
else
  echo "input: $compose_file (raw file scan - docker CLI unavailable here)"
fi

# --- Read one key's values out of one service's block. -----------------------
# Tracks the top-level `services:` mapping and the indentation of the wanted
# service's block, so a match under a *different* service (or under some other
# top-level key) can never satisfy an assertion. Prints one value per line;
# prints nothing when the service or the key is absent. Handles both the block
# list the compose file uses and the flow form (`key: ["a", "b"]`), so a
# reformat produces a real result rather than a phantom failure.
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
# A block-list item continues the list only while it stays inside the key.
in_list {
  if (ind > key_ind && text ~ /^- /) { print unquote(substr(text, 3)); next }
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

service_values() { # <service> <key> -> one value per line
  awk -v want="$1" -v key="$2" "$extract_awk" "$model"
}

# --- Self-check: prove the scan is actually service-scoped. ------------------
# Without this, an extractor bug that leaked values across service blocks would
# turn every assertion below into the file-wide grep this test exists to avoid:
# the flag on one service would satisfy both. Two services' `container_name`
# values must come back distinct and correct for the scoping to be real.
ild_name="$(service_values ild container_name)"
pg_name="$(service_values postgres container_name)"
if [ "$ild_name" = "ild" ] && [ "$pg_name" = "ild-postgres" ]; then
  pass "scan is service-scoped (ild -> '$ild_name', postgres -> '$pg_name')"
else
  echo "FAIL: service-block scan is broken - container_name read back as" \
       "ild='$ild_name' (want 'ild'), postgres='$pg_name' (want 'ild-postgres')."
  echo "      Every assertion below would be meaningless; fix the scan first."
  exit 1
fi

# --- The assertion. ----------------------------------------------------------
for service in $required_services; do
  opts="$(service_values "$service" security_opt)"
  if [ -z "$opts" ]; then
    fail "service '$service' sets no security_opt at all; it needs '$required_flag'" \
         "(a setuid bit or file capability inside the container would otherwise" \
         "still be an escalation route - see the header of this script)"
  elif printf '%s\n' "$opts" | grep -Fxq "$required_flag"; then
    pass "service '$service' runs with '$required_flag'"
  else
    fail "service '$service' has security_opt [$(printf '%s' "$opts" | tr '\n' ' ')]" \
         "but not '$required_flag'"
  fi
done

if [ "$failures" -ne 0 ]; then
  echo "$failures assertion(s) failed"; exit 1
fi
echo "all compose security_opt tests passed"
