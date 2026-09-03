#!/usr/bin/env bash
#
# Regression test for entrypoint.sh's agent egress rules (ADR-0019).
#
# The rules are what turns the egress proxy from advice into a boundary: they
# drop everything the agent uid sends except loopback and DNS, so a connection
# that skips HTTP_PROXY goes nowhere. They are installed once, as root, right
# before the privilege drop — and their absence has no runtime symptom at all.
# The agent still reaches the network through the proxy, the log still fills,
# and only the Settings page says "advisory". So the shape of the rule set, and
# the honesty of the enforced/advisory report, are pinned here.
#
# This sources the real functions out of entrypoint.sh and drives them against
# stub `nft` / `iptables` binaries that record what they were asked to install
# (no root, no netns, no real firewall). The capability probe reads a fake
# /proc/self/status, so both halves of the NET_ADMIN decision are exercised.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
entrypoint="$repo_root/entrypoint.sh"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# --- Load the real functions without running the entrypoint's top-level body.
funcs="$work/funcs.sh"
for fn in has_cap_net_admin install_egress_rules_nft install_egress_rules_iptables install_agent_egress_rules; do
  awk -v hdr="$fn() {" '$0==hdr{p=1} p{print} p&&$0=="}"{exit}' "$entrypoint" >> "$funcs"
  if ! grep -q "^$fn() {" "$funcs"; then
    echo "FAIL: could not extract $fn from $entrypoint"; exit 1
  fi
done
# shellcheck disable=SC1090
. "$funcs"

# --- Stubs. Each records its argv (and, for nft, its stdin) so the assertions
# read what the entrypoint asked the firewall to do.
mkdir -p "$work/bin"
cat > "$work/bin/nft" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$NFT_ARGS_LOG"
cat >> "$NFT_RULES_LOG"
exit "${NFT_EXIT:-0}"
STUB
cat > "$work/bin/iptables" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$IPT_LOG"
# `-C` (check) says "not present" so the jump gets inserted; `-N` succeeds.
case " $* " in *" -C "*) exit 1 ;; esac
exit 0
STUB
cp "$work/bin/iptables" "$work/bin/ip6tables"
chmod +x "$work/bin/"*
export NFT_ARGS_LOG="$work/nft-args" NFT_RULES_LOG="$work/nft-rules" IPT_LOG="$work/ipt"

# The uid probe: the entrypoint asks `id -u "$AGENT_USER"`. Model it.
id() { [ "$1" = "-u" ] && [ "$2" = "agent" ] && echo 10002 && return 0; return 1; }

with_caps() { # <CapEff hex>
  CAP_STATUS_FILE="$work/status"
  printf 'Name:\tsh\nCapEff:\t%s\n' "$1" > "$CAP_STATUS_FILE"
}

reset() {
  rm -f "$NFT_ARGS_LOG" "$NFT_RULES_LOG" "$IPT_LOG"
  unset ILD_NETWORK_ENFORCEMENT ILD_NETWORK_ENFORCEMENT_REASON
  AGENT_USER=agent
  ILD_NETWORK_PROXY_PORT=3128
  NFT_BIN="$work/bin/nft"
  IPTABLES_BIN="$work/bin/iptables"
  IP6TABLES_BIN="$work/bin/ip6tables"
}

# --- 1. The capability probe reads bit 12 of CapEff.
with_caps 000001ffffffffff
has_cap_net_admin && pass "CapEff with NET_ADMIN is detected" || fail "full CapEff not detected as holding NET_ADMIN"
with_caps 000001ffffffefff
has_cap_net_admin && fail "CapEff without bit 12 reported as NET_ADMIN" || pass "CapEff without NET_ADMIN is detected"
CAP_STATUS_FILE="$work/missing"
has_cap_net_admin && fail "unreadable status file reported as NET_ADMIN" || pass "an unreadable status file means no NET_ADMIN"

# --- 2. With NET_ADMIN and nft: the rules are installed, keyed on the agent uid,
# and the report says enforced.
reset; with_caps 000001ffffffffff
install_agent_egress_rules >/dev/null 2>&1
rules="$(cat "$NFT_RULES_LOG" 2>/dev/null)"
[ "${ILD_NETWORK_ENFORCEMENT:-}" = enforced ] && pass "nft path reports enforced" || fail "nft path reported '${ILD_NETWORK_ENFORCEMENT:-}', expected enforced"
case "${ILD_NETWORK_ENFORCEMENT_REASON:-}" in *10002*3128*) pass "enforced reason names the uid and the proxy port" ;; *) fail "enforced reason lacks uid/port: ${ILD_NETWORK_ENFORCEMENT_REASON:-}" ;; esac
grep -q 'meta skuid != 10002 accept' <<<"$rules" && pass "rules apply only to the agent uid" || fail "rules do not key on uid 10002: $rules"
grep -q 'oif "lo" accept' <<<"$rules" && pass "loopback (the proxy, ILD's API) stays open" || fail "no loopback accept"
grep -q 'udp dport 53 accept' <<<"$rules" && grep -q 'tcp dport 53 accept' <<<"$rules" && pass "DNS stays open" || fail "DNS not accepted"
grep -q 'counter drop' <<<"$rules" && pass "everything else is dropped" || fail "no terminal drop"
grep -q 'hook output' <<<"$rules" && pass "rules sit on the output hook" || fail "not an output-hook chain"
grep -q '^delete table inet ild_agent_egress' <<<"$rules" && pass "table is replaced, not stacked, on restart" || fail "no idempotent delete of the table"
# Ordering: accepts before the drop, or the drop wins.
drop_line="$(grep -n 'counter drop' <<<"$rules" | cut -d: -f1)"
lo_line="$(grep -n 'oif "lo" accept' <<<"$rules" | cut -d: -f1)"
[ -n "$drop_line" ] && [ -n "$lo_line" ] && [ "$lo_line" -lt "$drop_line" ] && pass "accepts precede the drop" || fail "drop precedes the accepts"
[ ! -e "$IPT_LOG" ] && pass "iptables untouched when nft succeeds" || fail "iptables also invoked on the nft path"

# --- 3. Without NET_ADMIN: nothing is installed, a warning is printed, and the
# report says advisory with a reason that tells the operator the knob.
reset; with_caps 000001ffffffefff
install_agent_egress_rules >/dev/null 2>"$work/stderr"
stderr="$(cat "$work/stderr")"
[ "${ILD_NETWORK_ENFORCEMENT:-}" = advisory ] && pass "missing NET_ADMIN reports advisory" || fail "missing NET_ADMIN reported '${ILD_NETWORK_ENFORCEMENT:-}'"
case "${ILD_NETWORK_ENFORCEMENT_REASON:-}" in *NET_ADMIN*cap-add*) pass "advisory reason names NET_ADMIN and how to grant it" ;; *) fail "advisory reason unhelpful: ${ILD_NETWORK_ENFORCEMENT_REASON:-}" ;; esac
case "$stderr" in *WARNING*) pass "a warning is printed on stderr" ;; *) fail "no warning printed without NET_ADMIN" ;; esac
[ ! -e "$NFT_RULES_LOG" ] && [ ! -e "$IPT_LOG" ] && pass "no firewall tool is invoked without NET_ADMIN" || fail "firewall tool invoked without NET_ADMIN"

# --- 4. nft missing: the iptables fallback installs the same shape, for v4 and v6.
reset; with_caps 000001ffffffffff
NFT_BIN="$work/bin/does-not-exist"
install_agent_egress_rules >/dev/null 2>&1
ipt="$(cat "$IPT_LOG" 2>/dev/null)"
[ "${ILD_NETWORK_ENFORCEMENT:-}" = enforced ] && pass "iptables fallback reports enforced" || fail "iptables fallback reported '${ILD_NETWORK_ENFORCEMENT:-}'"
grep -q -- '-I OUTPUT 1 -m owner --uid-owner 10002 -j ILD_AGENT_EGRESS' <<<"$ipt" && pass "iptables jump keys on the agent uid" || fail "iptables jump missing: $ipt"
grep -q -- '-A ILD_AGENT_EGRESS -o lo -j ACCEPT' <<<"$ipt" && pass "iptables keeps loopback open" || fail "iptables loopback accept missing"
grep -q -- '--dport 53 -j ACCEPT' <<<"$ipt" && pass "iptables keeps DNS open" || fail "iptables DNS accept missing"
grep -q -- '-A ILD_AGENT_EGRESS -j DROP' <<<"$ipt" && pass "iptables drops the rest" || fail "iptables terminal DROP missing"
[ "$(grep -c -- '-j DROP' <<<"$ipt")" = 2 ] && pass "rules installed for IPv4 and IPv6" || fail "expected one DROP per address family, got: $ipt"

# --- 5. nft present but failing, iptables absent: honest advisory, not a false enforced.
reset; with_caps 000001ffffffffff
NFT_EXIT=1; export NFT_EXIT
IPTABLES_BIN="$work/bin/none4"; IP6TABLES_BIN="$work/bin/none6"
install_agent_egress_rules >/dev/null 2>&1
unset NFT_EXIT
[ "${ILD_NETWORK_ENFORCEMENT:-}" = advisory ] && pass "a failed install reports advisory" || fail "a failed install reported '${ILD_NETWORK_ENFORCEMENT:-}'"

# --- 6. No proxy port: nothing to funnel into, advisory with a reason.
reset; with_caps 000001ffffffffff
ILD_NETWORK_PROXY_PORT=""
install_agent_egress_rules >/dev/null 2>&1
[ "${ILD_NETWORK_ENFORCEMENT:-}" = advisory ] && [ ! -e "$NFT_RULES_LOG" ] && pass "an empty proxy port installs nothing and reports advisory" || fail "empty proxy port: '${ILD_NETWORK_ENFORCEMENT:-}'"

if [ "$failures" -ne 0 ]; then
  echo "$failures assertion(s) failed"; exit 1
fi
echo "all entrypoint egress-rule tests passed"
