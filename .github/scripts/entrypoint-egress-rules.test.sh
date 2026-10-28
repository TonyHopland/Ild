#!/usr/bin/env bash
#
# Regression test for entrypoint.sh's agent egress rules (ADR-0019).
#
# The rules are what turns the egress proxy from advice into a boundary: they
# drop everything the agent uid sends except loopback, and drop DNS even on
# loopback, so a connection that skips HTTP_PROXY goes nowhere. They are
# installed once, as root, right before the privilege drop — and their absence
# has no runtime symptom at all.
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
for fn in has_cap_net_admin has_no_new_privs resolve_network_open_gid install_egress_rules_nft install_egress_rules_iptables install_agent_egress_rules; do
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

# The account probes: the entrypoint asks `id` and `getent` about the agent user and
# the groups. Modelled on the real output formats (checked against this host:
# `getent group root` is name:x:gid:members, `id -G` is space-separated gids).
# The groups: agent (primary, gid $PRIMARY_GID), ild-agents (shared, 10001), and
# ild-net-open, which exists only while OPEN_GID is set and lists $OPEN_MEMBERS.
PRIMARY_GID=10002; OPEN_GID=10004; OPEN_MEMBERS=""
agent_gids() { if [ -n "$OPEN_MEMBERS" ]; then echo "$PRIMARY_GID 10001 $OPEN_GID"; else echo "$PRIMARY_GID 10001"; fi; }
id() {
  case "$1 $2" in
    "-u agent") echo 10002 ;;
    "-g agent") echo "$PRIMARY_GID" ;;
    "-G agent") agent_gids ;;
    *) return 1 ;;
  esac
}
getent() {
  [ "$1" = group ] || { [ "$1" = passwd ] && [ "$2" = agent ] && echo "agent:x:10002:$PRIMARY_GID::/home/agent:/bin/sh" && return 0; return 2; }
  case "$2" in
    agent|"$PRIMARY_GID") echo "agent:x:$PRIMARY_GID:" ;;
    ild-agents|10001) echo "ild-agents:x:10001:ild" ;;
    ild-net-open) [ -n "$OPEN_GID" ] && echo "ild-net-open:x:$OPEN_GID:$OPEN_MEMBERS" || return 2 ;;
    *) if [ -n "$OPEN_GID" ] && [ "$2" = "$OPEN_GID" ]; then echo "ild-net-open:x:$OPEN_GID:$OPEN_MEMBERS"; else return 2; fi ;;
  esac
}

# <CapEff hex> [NoNewPrivs value; omitted = no such line]
with_status() {
  CAP_STATUS_FILE="$work/status"
  { printf 'Name:\tsh\n'; [ -n "${2:-}" ] && printf 'NoNewPrivs:\t%s\n' "$2"; printf 'CapEff:\t%s\n' "$1"; } > "$CAP_STATUS_FILE"
}

with_caps() { # <CapEff hex>
  CAP_STATUS_FILE="$work/status"
  printf 'Name:\tsh\nCapEff:\t%s\n' "$1" > "$CAP_STATUS_FILE"
}

reset() {
  rm -f "$NFT_ARGS_LOG" "$NFT_RULES_LOG" "$IPT_LOG"
  unset ILD_NETWORK_ENFORCEMENT ILD_NETWORK_ENFORCEMENT_REASON ILD_NETWORK_OPEN_GROUP NETWORK_OPEN_GROUP
  PRIMARY_GID=10002; OPEN_GID=10004; OPEN_MEMBERS=""
  AGENT_USER=agent
  AGENT_GROUP=agent
  SHARED_GROUP=ild-agents
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
! grep -E 'dport[^#]*53[^#]*accept' <<<"$rules" >/dev/null && pass "no rule accepts port 53" || fail "a rule accepts port 53: $rules"
dns_drop="$(grep -E 'dport 53.*drop' <<<"$rules")"
grep -q 'udp' <<<"$dns_drop" && grep -q 'tcp' <<<"$dns_drop" && pass "udp and tcp port 53 are dropped" || fail "port 53 not dropped for udp and tcp: $rules"
case "${ILD_NETWORK_ENFORCEMENT_REASON:-}" in *"and DNS"*|*"DNS allowed"*|*"DNS is open"*) fail "enforced reason still says DNS is allowed: ${ILD_NETWORK_ENFORCEMENT_REASON:-}" ;; *) pass "enforced reason does not advertise DNS as reachable" ;; esac
grep -q 'counter drop' <<<"$rules" && pass "everything else is dropped" || fail "no terminal drop"
grep -q 'hook output' <<<"$rules" && pass "rules sit on the output hook" || fail "not an output-hook chain"
grep -q '^delete table inet ild_agent_egress' <<<"$rules" && pass "table is replaced, not stacked, on restart" || fail "no idempotent delete of the table"
# Ordering: accepts before the drop, or the drop wins.
drop_line="$(grep -n 'counter drop' <<<"$rules" | cut -d: -f1)"
lo_line="$(grep -n 'oif "lo" accept' <<<"$rules" | cut -d: -f1)"
[ -n "$drop_line" ] && [ -n "$lo_line" ] && [ "$lo_line" -lt "$drop_line" ] && pass "accepts precede the drop" || fail "drop precedes the accepts"
# Loopback must not carry DNS (Docker's resolver lives on 127.0.0.11).
dns_line="$(grep -nE 'dport 53.*drop' <<<"$rules" | cut -d: -f1 | sort -n | tail -1)"
[ -n "$dns_line" ] && [ -n "$lo_line" ] && [ "$dns_line" -lt "$lo_line" ] && pass "port 53 drops precede the loopback accept" || fail "port 53 drop missing or after the loopback accept"
skuid_line="$(grep -n 'meta skuid != 10002 accept' <<<"$rules" | cut -d: -f1)"
[ -n "$skuid_line" ] && [ -n "$dns_line" ] && [ "$skuid_line" -lt "$dns_line" ] && pass "other uids are accepted before the port 53 drops" || fail "uid accept does not come first"
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
! grep -q -- '--dport 53 -j ACCEPT' <<<"$ipt" && pass "iptables accepts no port 53" || fail "iptables accepts port 53: $ipt"
grep -q -- '-A ILD_AGENT_EGRESS -j DROP' <<<"$ipt" && pass "iptables drops the rest" || fail "iptables terminal DROP missing"
[ "$(grep -c -- '-A ILD_AGENT_EGRESS -j DROP' <<<"$ipt")" = 2 ] && pass "terminal DROP installed for IPv4 and IPv6" || fail "expected one terminal DROP per address family, got: $ipt"
[ "$(grep -c -- '-p udp --dport 53 -j DROP' <<<"$ipt")" = 2 ] && [ "$(grep -c -- '-p tcp --dport 53 -j DROP' <<<"$ipt")" = 2 ] && pass "udp and tcp port 53 dropped for IPv4 and IPv6" || fail "expected udp and tcp port 53 DROPs per family, got: $ipt"
# Each family builds its own chain (-N); within it both 53 drops precede the loopback accept.
awk '
  / -N ILD_AGENT_EGRESS/ { fam++; n53 = 0 }
  /-A ILD_AGENT_EGRESS -p (udp|tcp) --dport 53 -j DROP/ { n53++ }
  /-A ILD_AGENT_EGRESS -o lo -j ACCEPT/ { lo++; if (n53 < 2) bad = 1 }
  END { exit (fam == 2 && lo == 2 && !bad) ? 0 : 1 }
' <<<"$ipt" && pass "port 53 drops precede the loopback accept in each family" || fail "port 53 drops not before the loopback accept per family: $ipt"

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

# =====================================================================
# Off mode (ADR-0019): an extra accept for the agent uid AND the open group,
# installed only under no-new-privileges. Three groups are in play and must never
# be mixed up: the agent's primary group (what proxied launches run under), the
# shared group (a supplementary group, never --regid), and the open group (the
# --regid of an Off launch only; the agent is no member of it).
# =====================================================================
FULL=000001ffffffffff

# --- 7. The probe for no-new-privileges reads NoNewPrivs from the same status file.
with_status "$FULL" 1
has_no_new_privs && pass "NoNewPrivs 1 is detected" || fail "NoNewPrivs 1 not detected"
with_status "$FULL" 0
has_no_new_privs && fail "NoNewPrivs 0 reported as set" || pass "NoNewPrivs 0 is not set"
with_status "$FULL"
has_no_new_privs && fail "a status file without NoNewPrivs reported as set" || pass "a status file without a NoNewPrivs line is not set"
CAP_STATUS_FILE="$work/missing"
has_no_new_privs && fail "unreadable status file reported as NoNewPrivs" || pass "an unreadable status file is not NoNewPrivs"

# --- 8. Which group may be enabled: the gid is printed only when every check holds.
resolve() { # prints "<gid>|<stderr>"
  out="$(resolve_network_open_gid 2>"$work/resolve-err")"; err="$(cat "$work/resolve-err")"
  echo "$out|$err"
}

reset; with_status "$FULL" 1; NETWORK_OPEN_GROUP=ild-net-open
r="$(resolve)"
[ "${r%%|*}" = 10004 ] && [ -z "${r#*|}" ] && pass "a valid open group resolves to its gid without noise" || fail "valid open group resolved to: $r"

reset; with_status "$FULL" 1
r="$(resolve)"
[ -z "${r%%|*}" ] && pass "no open group configured resolves to nothing" || fail "unset NETWORK_OPEN_GROUP resolved to: $r"

reset; with_status "$FULL"; NETWORK_OPEN_GROUP=ild-net-open
r="$(resolve)"
[ -z "${r%%|*}" ] && pass "the group is not enabled without no-new-privileges" || fail "group enabled without NNP: $r"
case "${r#*|}" in *WARNING*no-new-privileges*|*WARNING*NoNewPrivs*) pass "the missing no-new-privileges is warned about by name" ;; *) fail "no NNP warning naming it: ${r#*|}" ;; esac

reset; with_status "$FULL" 0; NETWORK_OPEN_GROUP=ild-net-open
r="$(resolve)"
[ -z "${r%%|*}" ] && pass "NoNewPrivs 0 does not enable the group" || fail "group enabled with NoNewPrivs 0: $r"

reset; with_status "$FULL" 1; NETWORK_OPEN_GROUP=ild-net-open; OPEN_GID=""
r="$(resolve)"
[ -z "${r%%|*}" ] && pass "a group that does not exist is not enabled" || fail "missing group enabled: $r"
case "${r#*|}" in *WARNING*) pass "a missing group is warned about" ;; *) fail "no warning for a missing group" ;; esac

reset; with_status "$FULL" 1; NETWORK_OPEN_GROUP=ild-net-open; OPEN_GID=10002
r="$(resolve)"
[ -z "${r%%|*}" ] && pass "the agent's primary gid is refused as the open group" || fail "primary gid accepted as open group: $r"
case "${r#*|}" in *WARNING*) pass "the primary-gid clash is warned about" ;; *) fail "no warning for primary gid clash" ;; esac

reset; with_status "$FULL" 1; NETWORK_OPEN_GROUP=ild-net-open; OPEN_GID=10001
r="$(resolve)"
[ -z "${r%%|*}" ] && pass "the shared group's gid is refused as the open group" || fail "shared gid accepted as open group: $r"
case "${r#*|}" in *WARNING*) pass "the shared-gid clash is warned about" ;; *) fail "no warning for shared gid clash" ;; esac

reset; with_status "$FULL" 1; NETWORK_OPEN_GROUP=ild-net-open; OPEN_MEMBERS=agent
r="$(resolve)"
[ -z "${r%%|*}" ] && pass "a group the agent is a member of is refused" || fail "group with the agent as member accepted: $r"
case "${r#*|}" in *WARNING*) pass "the membership is warned about" ;; *) fail "no warning for agent membership" ;; esac

# --- 9. nft: the accept for (agent uid, open gid) sits after the other-uid accept and
# before both port-53 drops, the loopback accept and the terminal drop.
reset
install_egress_rules_nft 10002 10004 >/dev/null 2>&1
rules="$(cat "$NFT_RULES_LOG" 2>/dev/null)"
open_line="$(grep -nE 'skuid 10002.*skgid 10004.*accept|skgid 10004.*skuid 10002.*accept' <<<"$rules" | cut -d: -f1 | head -1)"
other_line="$(grep -n 'meta skuid != 10002 accept' <<<"$rules" | cut -d: -f1)"
first53="$(grep -nE 'dport 53.*drop' <<<"$rules" | cut -d: -f1 | sort -n | head -1)"
lo_line="$(grep -n 'oif "lo" accept' <<<"$rules" | cut -d: -f1)"
drop_line="$(grep -n 'counter drop' <<<"$rules" | cut -d: -f1)"
[ -n "$open_line" ] && pass "nft installs an accept keyed on the agent uid and the open gid" || fail "no (uid, gid) accept in nft rules: $rules"
[ -n "$open_line" ] && [ -n "$other_line" ] && [ "$other_line" -lt "$open_line" ] && pass "nft open-group accept follows the other-uid accept" || fail "open-group accept not after 'meta skuid != uid accept'"
[ -n "$open_line" ] && [ -n "$first53" ] && [ "$open_line" -lt "$first53" ] && pass "nft open-group accept precedes the port 53 drops" || fail "open-group accept not before the port 53 drops"
[ -n "$open_line" ] && [ -n "$lo_line" ] && [ "$open_line" -lt "$lo_line" ] && [ -n "$drop_line" ] && [ "$open_line" -lt "$drop_line" ] && pass "nft open-group accept precedes loopback accept and the terminal drop" || fail "open-group accept not before loopback accept and terminal drop"
[ "$(grep -c skgid <<<"$rules")" = 1 ] && pass "exactly one rule mentions the gid" || fail "gid mentioned by other rules: $rules"

# Without the group the nft input is byte-identical to what shipped before Off.
expected_nft='table inet ild_agent_egress
delete table inet ild_agent_egress
table inet ild_agent_egress {
  chain output {
    type filter hook output priority filter; policy accept;
    meta skuid != 10002 accept
    udp dport 53 drop
    tcp dport 53 drop
    oif "lo" accept
    counter drop
  }
}'
reset
install_egress_rules_nft 10002 >/dev/null 2>&1
[ "$(cat "$NFT_RULES_LOG")" = "$expected_nft" ] && pass "nft rules are unchanged without the open group" || fail "nft rules changed without the open group: $(cat "$NFT_RULES_LOG")"
reset
install_egress_rules_nft 10002 "" >/dev/null 2>&1
[ "$(cat "$NFT_RULES_LOG")" = "$expected_nft" ] && pass "an empty gid is the same as none for nft" || fail "empty gid changed the nft rules"

# --- 10. iptables: the same accept, first in each family's chain, no --suppl-groups.
ipt_base() { # <extra line or empty>
  for fam in 1 2; do
    echo "-w -N ILD_AGENT_EGRESS"
    echo "-w -C OUTPUT -m owner --uid-owner 10002 -j ILD_AGENT_EGRESS"
    echo "-w -I OUTPUT 1 -m owner --uid-owner 10002 -j ILD_AGENT_EGRESS"
    [ -n "$1" ] && echo "$1"
    echo "-w -A ILD_AGENT_EGRESS -p udp --dport 53 -j DROP"
    echo "-w -A ILD_AGENT_EGRESS -p tcp --dport 53 -j DROP"
    echo "-w -A ILD_AGENT_EGRESS -o lo -j ACCEPT"
    echo "-w -A ILD_AGENT_EGRESS -j DROP"
  done
}
reset
install_egress_rules_iptables 10002 10004 >/dev/null 2>&1
[ "$(cat "$IPT_LOG")" = "$(ipt_base '-w -A ILD_AGENT_EGRESS -m owner --uid-owner 10002 --gid-owner 10004 -j ACCEPT')" ] \
  && pass "iptables adds the (uid, gid) accept before the 53 drops, loopback accept and terminal drop in both families" \
  || fail "iptables rules with the open group are not as expected: $(cat "$IPT_LOG")"
grep -q -- '--suppl-groups' "$IPT_LOG" && fail "iptables rule matches supplementary groups" || pass "iptables rule matches the effective gid only"

reset
install_egress_rules_iptables 10002 >/dev/null 2>&1
[ "$(cat "$IPT_LOG")" = "$(ipt_base '')" ] && pass "iptables commands are unchanged without the open group" || fail "iptables commands changed without the open group: $(cat "$IPT_LOG")"

# --- 11. The whole install, the way the entrypoint runs it.
check_other_rules_intact() { # <label>
  r="$(cat "$NFT_RULES_LOG" 2>/dev/null)"
  grep -q 'meta skuid != 10002 accept' <<<"$r" && grep -q 'counter drop' <<<"$r" && [ "$(grep -c 'dport 53.*drop' <<<"$r")" = 2 ] \
    && pass "$1: the other rules are still installed" || fail "$1: the other rules are missing: $r"
}

reset; with_status "$FULL" 1; NETWORK_OPEN_GROUP=ild-net-open
install_agent_egress_rules >/dev/null 2>"$work/stderr"
rules="$(cat "$NFT_RULES_LOG" 2>/dev/null)"
grep -qE 'skuid 10002.*skgid 10004|skgid 10004.*skuid 10002' <<<"$rules" && pass "nft path installs the open-group rule under NNP" || fail "no open-group rule under NNP: $rules"
[ "${ILD_NETWORK_OPEN_GROUP:-}" = ild-net-open ] && pass "the open group is exported to the app once its rule is installed" || fail "ILD_NETWORK_OPEN_GROUP is '${ILD_NETWORK_OPEN_GROUP:-}'"
[ "${ILD_NETWORK_ENFORCEMENT:-}" = enforced ] && pass "the report is enforced with the open group" || fail "reported '${ILD_NETWORK_ENFORCEMENT:-}' with the open group"
case "${ILD_NETWORK_ENFORCEMENT_REASON:-}" in *10002*3128*) pass "the reason still names the uid and the port" ;; *) fail "reason lost uid/port: ${ILD_NETWORK_ENFORCEMENT_REASON:-}" ;; esac
case "${ILD_NETWORK_ENFORCEMENT_REASON:-}" in *"and DNS"*|*"DNS allowed"*|*"DNS is open"*) fail "reason advertises DNS: ${ILD_NETWORK_ENFORCEMENT_REASON:-}" ;; *) pass "the reason does not advertise DNS as reachable" ;; esac

# The iptables backend gets the same rule through the full install.
reset; with_status "$FULL" 1; NETWORK_OPEN_GROUP=ild-net-open; NFT_BIN="$work/bin/does-not-exist"
install_agent_egress_rules >/dev/null 2>&1
[ "$(grep -c -- '--uid-owner 10002 --gid-owner 10004 -j ACCEPT' "$IPT_LOG")" = 2 ] && [ "${ILD_NETWORK_OPEN_GROUP:-}" = ild-net-open ] \
  && pass "iptables path installs the open-group rule for both families and exports the group" || fail "iptables path: $(cat "$IPT_LOG" 2>/dev/null) / '${ILD_NETWORK_OPEN_GROUP:-}'"

# Without NNP: everything else installs, Off is reported unavailable, nothing is exported.
reset; with_status "$FULL"; NETWORK_OPEN_GROUP=ild-net-open
install_agent_egress_rules >/dev/null 2>"$work/stderr"
rules="$(cat "$NFT_RULES_LOG" 2>/dev/null)"
grep -q skgid <<<"$rules" && fail "an open-group rule was installed without NNP: $rules" || pass "no open-group rule without NNP"
[ -z "${ILD_NETWORK_OPEN_GROUP:-}" ] && pass "nothing is exported without NNP" || fail "exported '${ILD_NETWORK_OPEN_GROUP:-}' without NNP"
[ "${ILD_NETWORK_ENFORCEMENT:-}" = enforced ] && pass "the report stays enforced without NNP" || fail "reported '${ILD_NETWORK_ENFORCEMENT:-}' without NNP"
check_other_rules_intact "without NNP"
reason="${ILD_NETWORK_ENFORCEMENT_REASON:-}"
case "$reason" in *"Off"*unavailable*|*unavailable*"Off"*) pass "the reason says Off is unavailable" ;; *) fail "reason does not say Off is unavailable: $reason" ;; esac
case "$reason" in *no-new-privileges*) pass "the reason names no-new-privileges as the cause" ;; *) fail "reason does not name no-new-privileges: $reason" ;; esac
case "$reason" in *"no network"*) pass "the reason says Off agents have no network" ;; *) fail "reason does not warn that Off agents have no network: $reason" ;; esac
case "$reason" in *10002*3128*) pass "the reason still names the uid and the port without NNP" ;; *) fail "reason lost uid/port without NNP: $reason" ;; esac
case "$(cat "$work/stderr")" in *WARNING*) pass "a warning is printed without NNP" ;; *) fail "no warning without NNP" ;; esac

# Misconfigured group (here: the agent is a member): same fail-safe.
reset; with_status "$FULL" 1; NETWORK_OPEN_GROUP=ild-net-open; OPEN_MEMBERS=agent
install_agent_egress_rules >/dev/null 2>&1
grep -q skgid "$NFT_RULES_LOG" 2>/dev/null && fail "open-group rule installed for a group the agent belongs to" || pass "no open-group rule for a group the agent belongs to"
[ -z "${ILD_NETWORK_OPEN_GROUP:-}" ] && [ "${ILD_NETWORK_ENFORCEMENT:-}" = enforced ] && pass "a bad group exports nothing and the report stays enforced" || fail "bad group: '${ILD_NETWORK_OPEN_GROUP:-}' / '${ILD_NETWORK_ENFORCEMENT:-}'"
case "${ILD_NETWORK_ENFORCEMENT_REASON:-}" in *unavailable*) pass "a bad group says Off is unavailable" ;; *) fail "bad group reason: ${ILD_NETWORK_ENFORCEMENT_REASON:-}" ;; esac
check_other_rules_intact "bad group"

# Not configured at all: the report and rules are as before, nothing exported.
reset; with_status "$FULL" 1
install_agent_egress_rules >/dev/null 2>&1
[ "$(cat "$NFT_RULES_LOG")" = "$expected_nft" ] && [ -z "${ILD_NETWORK_OPEN_GROUP:-}" ] && pass "no open group configured: rules as before and nothing exported" || fail "unconfigured open group changed behaviour"

# The failed-install and advisory paths never export the group either.
reset; with_status "$FULL" 1; NETWORK_OPEN_GROUP=ild-net-open
NFT_EXIT=1; export NFT_EXIT; IPTABLES_BIN="$work/bin/none4"; IP6TABLES_BIN="$work/bin/none6"
install_agent_egress_rules >/dev/null 2>&1
unset NFT_EXIT
[ -z "${ILD_NETWORK_OPEN_GROUP:-}" ] && [ "${ILD_NETWORK_ENFORCEMENT:-}" = advisory ] && pass "a failed install exports no open group" || fail "failed install: '${ILD_NETWORK_OPEN_GROUP:-}' / '${ILD_NETWORK_ENFORCEMENT:-}'"
reset; with_status 000001ffffffefff 1; NETWORK_OPEN_GROUP=ild-net-open
install_agent_egress_rules >/dev/null 2>&1
[ -z "${ILD_NETWORK_OPEN_GROUP:-}" ] && [ ! -e "$NFT_RULES_LOG" ] && pass "without NET_ADMIN no open group is exported and no rule installed" || fail "no-NET_ADMIN path exported '${ILD_NETWORK_OPEN_GROUP:-}'"

# --- 12. An inherited ILD_NETWORK_OPEN_GROUP never reaches the app: the entrypoint
# clears it before any branch. This is the non-root, single-uid exec path (the same
# one the smoke test runs), where no firewall code runs at all.
unset -f id getent
if [ "$(id -u)" -ne 0 ]; then
  out="$(env -u ILD_DB_CONNECTION_STRING -u WORKITEM_DB_CONNECTION_STRING -u AGENT_USER ILD_NETWORK_OPEN_GROUP=ild-net-open \
    sh "$entrypoint" sh -c 'echo "[${ILD_NETWORK_OPEN_GROUP-unset}]"' 2>&1)"
  [ "$out" = "[unset]" ] && pass "an inherited ILD_NETWORK_OPEN_GROUP is cleared on the non-root path" || fail "inherited ILD_NETWORK_OPEN_GROUP reached the command: $out"
else
  echo "skipped: the inherited-variable check needs an unprivileged run"
fi

if [ "$failures" -ne 0 ]; then
  echo "$failures assertion(s) failed"; exit 1
fi
echo "all entrypoint egress-rule tests passed"
