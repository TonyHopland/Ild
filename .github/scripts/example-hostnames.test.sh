#!/usr/bin/env bash
#
# Tests for check-example-hostnames.sh. Run as a CI step in ci.yml.
#
# The repository is public, so hostnames and addresses committed as examples or
# test data must be ones that say nothing about a real network: reserved example
# names (RFC 2606/6761), documentation-range IPs, or public services the code
# genuinely talks to. Each fixture below is a throwaway git repo; the guard must
# reject the bad ones naming file:line, accept the good one, and accept this tree.
#
# This file is excluded from the guard's scan: it necessarily contains the hosts
# the guard forbids.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
script="$here/check-example-hostnames.sh"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# make_fixture <name> <relative path> <content>: a git repo with one tracked file.
make_fixture() {
  local dir="$work/$1"
  mkdir -p "$dir/$(dirname "$2")"
  printf '%s\n' "$3" > "$dir/$2"
  git -C "$dir" init -q
  git -C "$dir" add -A
  printf '%s' "$dir"
}

# expect_reject <desc> <relative path> <line> <host> <content>
expect_reject() {
  local desc="$1" path="$2" line="$3" host="$4" content="$5"
  local dir out
  fixture_count=$((fixture_count + 1))
  dir="$(make_fixture "reject-$fixture_count" "$path" "$content")"
  if out="$("$script" "$dir" 2>&1)"; then
    fail "$desc -> expected non-zero exit, got success"; return
  fi
  if ! grep -qF "$path:$line" <<<"$out"; then
    fail "$desc -> output does not name $path:$line:"; printf '      %s\n' "$out"; return
  fi
  if ! grep -qiF "$host" <<<"$out"; then
    fail "$desc -> output does not name the host $host:"; printf '      %s\n' "$out"; return
  fi
  if ! grep -qF "example domain" <<<"$out"; then
    fail "$desc -> output does not tell the author to use an example domain:"
    printf '      %s\n' "$out"; return
  fi
  pass "$desc -> rejected, naming $path:$line"
}

# expect_accept <desc> <dir>
expect_accept() {
  local desc="$1" dir="$2" out
  if ! out="$("$script" "$dir" 2>&1)"; then
    fail "$desc -> expected success, got non-zero exit:"; printf '      %s\n' "$out"; return
  fi
  pass "$desc -> accepted"
}

fixture_count=0
allowed_preamble='// Clone from https://git.example.com/team/repo.git or http://ild.example:8080'

expect_reject "non-reserved URL host" "src/BuildLinks.cs" 2 "build.acme-corp.io" \
  "$allowed_preamble
See http://build.acme-corp.io/x for the build."

expect_reject "user@host with a non-reserved domain" "src/Seed.cs" 2 "acme-corp.io" \
  "$allowed_preamble
var email = \"dev@acme-corp.io\";"

expect_reject "bare lowercase private-suffix name" "ILD.Tests/NasTests.cs" 3 "nas.lan" \
  "$allowed_preamble
// nothing here
Assert.Equal(\"nas.lan\", host);"

expect_reject "bare all-uppercase private-suffix name" "ILD.Tests/NasTests.cs" 2 "nas.lan" \
  "$allowed_preamble
Assert.True(Matches(\"NAS.LAN\"));"

expect_reject "bare private IP 192.168/16" "frontend/src/a.test.tsx" 2 "192.168.1.20" \
  "$allowed_preamble
const addr = '192.168.1.20';"

expect_reject "private IP 10/8 as a URL host" "config/appsettings.json" 2 "10.1.2.3" \
  "$allowed_preamble
Point it at http://10.1.2.3:8080/api."

expect_reject "private IP at the top of 172.16/12" "ILD.Tests/EgressTests.cs" 2 "172.31.255.254" \
  "$allowed_preamble
Allow(\"172.31.255.254\");"

# A tree the guard cannot list must fail, never pass as clean after scanning nothing.
not_a_repo="$work/not-a-repo"
mkdir -p "$not_a_repo"
printf '%s\n' 'See http://build.acme-corp.io/x' > "$not_a_repo/notes.txt"
if "$script" "$not_a_repo" >/dev/null 2>&1; then
  fail "a directory that is not a git repository -> expected non-zero exit, got success"
else
  pass "a directory that is not a git repository -> refused"
fi

good="$(make_fixture good "ILD.Tests/GoodTests.cs" \
'// Only reserved, documentation, loopback and allowlisted public hosts.
var a = "https://git.example.com/team/repo.git";
var b = "https://docs.example.org/x";
var c = "http://example.net";
var d = "http://wi-7-api.ild.example:8080";
var e = "WI-7.ILD.EXAMPLE";
var f = "https://forge.test/pr/1";
var g = "http://nothing.invalid";
var h = "http://localhost:5100 and http://ild.localhost:3100";
var i = "http://127.0.0.1:5000 and 0.0.0.0:80";
var j = "203.0.113.7, 192.0.2.5, 198.51.100.3";
var k = "https://github.com/owner/repo and https://api.github.com/repos";
var l = "https://contoso.visualstudio.com/project";
var m = "user@example.com";
var n = "https://forgejo/pr/1 and postgres";
var o = "http://{host}:{port}/ and https://$HOST/path";
var p = "VisualStudioVersion = 10.0.40219.1; version 10.0.4";
var q = "Foo.Bar.Internal and Microsoft.AspNetCore.SignalR.Internal";
var r = "172.32.0.1";')"
expect_accept "only allowed hosts, documentation IPs, version strings and identifiers" "$good"

expect_accept "the real tree" "$repo_root"

if [[ "$failures" -ne 0 ]]; then
  echo "$failures test(s) failed"; exit 1
fi
echo "all example hostname tests passed"
