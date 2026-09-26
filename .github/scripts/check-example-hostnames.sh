#!/usr/bin/env bash
#
# Fails when a tracked text file names a host or address outside the reserved
# example ranges.
#
# Usage: check-example-hostnames.sh [repo-root]   (default: the enclosing repo)
#
# The repository is public. A real private hostname or address committed as an
# example or as test data says what that network is called and how it is laid
# out, and no example needs one. Allowed:
#   - example.com/.net/.org and their subdomains, *.example, *.test, *.invalid,
#     localhost and *.localhost (RFC 2606, RFC 6761);
#   - loopback 127/8, 0.0.0.0, and the documentation ranges 192.0.2/24,
#     198.51.100/24 and 203.0.113/24 (RFC 5737);
#   - the public services in ALLOWED_HOSTS below.
#
# What is looked at: URL hosts, user@host, bare dotted names ending in a
# private-network suffix, and private IPv4 addresses anywhere. Single-label hosts
# (service names such as postgres) and templated hosts are not hostnames anyone
# can resolve outside their own setup, so they are left alone.
set -uo pipefail

# Real public hosts the code, docs or tooling genuinely talk to, one per line.
# An entry starting with a dot matches any subdomain of it.
ALLOWED_HOSTS='
github.com
api.github.com
docs.github.com
github.githubassets.com
dev.azure.com
.visualstudio.com
gitlab.com
api.openai.com
generativelanguage.googleapis.com
registry.npmjs.org
nodejs.org
dl.google.com
www.google.com
blog.google
claude.ai
www.anthropic.com
docs.anthropic.com
opencode.ai
modelcontextprotocol.io
viteplus.dev
keepachangelog.com
semver.org
www.w3.org
json-schema.org
json.schemastore.org
'

# Names the tests of this guard necessarily contain; relative to the repo root.
EXCLUDED_FILES='
.github/scripts/example-hostnames.test.sh
'

# A guard that cannot read the tree must not report it clean.
die() { echo "check-example-hostnames: $*" >&2; exit 2; }

root="${1:-}"
if [[ -z "$root" ]]; then
  root="$(git rev-parse --show-toplevel)" || die "not inside a git repository"
fi

is_allowed_name() { # <lowercase host>
  case "$1" in
    localhost | *.localhost | *.example | *.test | *.invalid) return 0 ;;
    example.com | *.example.com | example.net | *.example.net | example.org | *.example.org) return 0 ;;
  esac
  local entry
  while IFS= read -r entry; do
    case "$entry" in
      '') ;;
      .*) [[ "$1" == *"$entry" ]] && return 0 ;;
      *) [[ "$1" == "$entry" ]] && return 0 ;;
    esac
  done <<<"$ALLOWED_HOSTS"
  return 1
}

# Prints private, allowed or public for a valid dotted quad; nothing otherwise.
ipv4_class() { # <candidate>
  [[ "$1" =~ ^([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})$ ]] || return 0
  local a=$((10#${BASH_REMATCH[1]})) b=$((10#${BASH_REMATCH[2]})) c=$((10#${BASH_REMATCH[3]}))
  local octet
  for octet in "${BASH_REMATCH[@]:1}"; do
    ((10#$octet <= 255)) || return 0
  done
  if ((a == 10)) || ((a == 172 && b >= 16 && b <= 31)) || ((a == 192 && b == 168)); then
    echo private
  elif ((a == 127)) || [[ "$1" == 0.0.0.0 ]] ||
    ((a == 192 && b == 0 && c == 2)) || ((a == 198 && b == 51 && c == 100)) ||
    ((a == 203 && b == 0 && c == 113)); then
    echo allowed
  else
    echo public
  fi
}

offenders=()
report() { # <path> <line> <host>
  offenders+=("$1:$2: $3 — use an example domain (example.com, *.example, *.test, *.invalid)")
}

# Classifies one grep match. url and email matches still carry their prefix.
consider() { # <kind> <path> <line> <match>
  local kind="$1" path="$2" line="$3" host="$4"
  case "$kind" in
    url)
      host="${host#*://}"
      host="${host##*@}"
      ;;
    email) host="${host#*@}" ;;
    suffix)
      # Case-insensitive match, but a mixed-case dotted name is a code identifier.
      [[ "$host" == "${host,,}" || "$host" == "${host^^}" ]] || return 0
      ;;
    ipv4)
      # A leading dot means the run continues a longer dotted name.
      [[ "$host" == .* ]] && return 0
      [[ "$(ipv4_class "$host")" == private ]] && report "$path" "$line" "$host"
      return 0
      ;;
  esac
  host="${host,,}"
  host="${host%%.}"
  [[ "$host" == *.* ]] || return 0
  [[ "$host" == *[\{\$%]* ]] && return 0
  local ip_class
  ip_class="$(ipv4_class "$host")"
  if [[ -n "$ip_class" ]]; then
    [[ "$ip_class" != allowed ]] && report "$path" "$line" "$host"
  elif ! is_allowed_name "$host"; then
    report "$path" "$line" "$host"
  fi
}

files=()
while IFS= read -r -d '' file; do
  grep -qxF -- "$file" <<<"$EXCLUDED_FILES" && continue
  # Submodules and symlinks are not file content, and a tracked file deleted
  # from the work tree has none left to read.
  [[ -f "$root/$file" && ! -L "$root/$file" ]] && files+=("$file")
done < <(git -C "$root" ls-files -z)
wait $! || die "cannot list the tracked files of $root"

scan() { # <kind> <grep flags> <extended regex>
  local kind="$1" flags="$2" regex="$3" path rest
  ((${#files[@]})) || return 0
  while IFS= read -r -d '' path && IFS= read -r rest; do
    consider "$kind" "$path" "${rest%%:*}" "${rest#*:}"
  done < <(cd "$root" && printf '%s\0' "${files[@]}" |
    xargs -0 sh -c 'grep "$@"; [ $? -le 1 ]' grep -HnoIZ "$flags" -e "$regex" --)
  # grep exits 1 when nothing matched; above that it failed to read a file.
  wait $! || die "grep failed while scanning $root"
}

scan url -E '[A-Za-z][A-Za-z0-9+.-]*://([^/@[:space:]]*@)?[A-Za-z0-9._{}$%-]+'
scan email -E '[A-Za-z0-9._%+-]@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b'
scan suffix -iE '\b[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.(kube|lan|local|internal|home|corp|intranet|private|localdomain|arpa)\b'
scan ipv4 -E '\.?[0-9]+(\.[0-9]+){3,}'

if ((${#offenders[@]})); then
  printf '%s\n' "${offenders[@]}" | sort -t: -k1,1 -k2,2n -k3 -u
  exit 1
fi
