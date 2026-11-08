#!/usr/bin/env bash
#
# Tests for docker-compose.container-runs.yml, the overlay that runs each loop run, chat and provider sign-in in a
# container of its own (ILD_RUN_HOST=container).
#
# What the overlay promises, each of which fails at run time with nothing on screen if it drifts:
#   1. It refuses to start without ILD_CONTAINER_SOCKET_HOST, saying where to set it: the socket is a trust
#      decision ILD never makes for the operator.
#   2. ILD names its own container as the base file's container_name, or it cannot join a run's network.
#   3. ILD_API_URL names ILD by that container name, which each run's container is given ILD's address for.
#   4. ILD runs from the image the overlay's runner service builds, and that service builds the runner target.
#   5. ILD's port is the same inside its container and outside, and the preview base names that port: a preview
#      URL is opened by a browser outside and by a chat's browser inside.
#
# Input: `docker compose config` over both files where the docker CLI is available, with dummies for the base
# file's required variables; otherwise the two files are scanned raw, and that is said.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
base="$repo_root/docker-compose.yml"
overlay="$repo_root/docker-compose.container-runs.yml"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

[ -f "$overlay" ] || { echo "FAIL: $overlay is missing"; exit 1; }

# 1. The socket is required, by name and with a message, in the overlay itself.
if grep -q '\${ILD_CONTAINER_SOCKET_HOST:?[^}]*\.env' "$overlay"; then
  pass "the overlay refuses to start without ILD_CONTAINER_SOCKET_HOST and says to set it in .env"
else
  fail "the socket mount is not '\${ILD_CONTAINER_SOCKET_HOST:?...set it in .env...}'"
fi

dummies() {
  {
    grep -o '\${[A-Za-z_][A-Za-z0-9_]*:?' "$base" "$overlay" | sed 's/^.*\${//; s/:?$//'
    grep -o '\${[A-Za-z_][A-Za-z0-9_]*}' "$base" "$overlay" | sed 's/^.*\${//; s/}$//'
  } | sort -u | grep -v -x ILD_CONTAINER_SOCKET_HOST | sed 's/$/=ci-dummy/'
}

model=""
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  resolved="$(mktemp)"; errors="$(mktemp)"
  trap 'rm -f "$resolved" "$errors"' EXIT
  # shellcheck disable=SC2046  # word splitting is how the assignments are passed
  if (cd "$repo_root" && env -u ILD_CONTAINER_SOCKET_HOST $(dummies) \
        docker compose -f "$base" -f "$overlay" config) >/dev/null 2>"$errors"; then
    fail "docker compose config resolved the overlay with no ILD_CONTAINER_SOCKET_HOST"
  elif grep -q 'ILD_CONTAINER_SOCKET_HOST' "$errors"; then
    pass "without the socket, compose stops and names ILD_CONTAINER_SOCKET_HOST"
  else
    fail "without the socket, compose failed for another reason: $(cat "$errors")"
  fi
  # shellcheck disable=SC2046
  if ! (cd "$repo_root" && env $(dummies) ILD_CONTAINER_SOCKET_HOST=/run/podman/podman.sock \
        docker compose -f "$base" -f "$overlay" config) >"$resolved" 2>"$errors"; then
    echo "FAIL: docker compose config could not resolve the two files:"; cat "$errors"; exit 1
  fi
  model="$resolved"
  echo "input: docker compose config over docker-compose.yml and docker-compose.container-runs.yml"
else
  model="$(mktemp)"; trap 'rm -f "$model"' EXIT
  cat "$base" "$overlay" > "$model"
  echo "input: the two files scanned raw (docker CLI unavailable here)"
fi

# The value of an environment entry, in either the list form the files use or the map form compose config prints.
env_value() { # <name>
  sed -n -e "s/^[[:space:]]*- $1=\(.*\)$/\1/p" -e "s/^[[:space:]]*$1: \"\{0,1\}\([^\"]*\)\"\{0,1\}$/\1/p" "$model" | tail -n1
}

# 2. ILD names its own container as the base file names it.
container_name="$(sed -n '/^  ild:/,/^  [a-z]/s/^    container_name: *\(.*\)$/\1/p' "$base" | head -n1)"
named="$(env_value ILD_CONTAINER_ILD_CONTAINER)"
if [ -n "$container_name" ] && [ "$named" = "$container_name" ]; then
  pass "ILD_CONTAINER_ILD_CONTAINER is the ild service's container_name ($container_name)"
else
  fail "ILD_CONTAINER_ILD_CONTAINER='$named' but the ild service's container_name is '$container_name'"
fi

# 3. ILD_API_URL names ILD by that container name, on the port ILD listens on.
api_url="$(env_value ILD_API_URL)"
ild_section="$(sed -n '/^  ild:/,/^  [a-z]/p' "$base")"
listen_port="$(printf '%s\n' "$ild_section" | sed -n 's/^[[:space:]]*- ASPNETCORE_URLS=http:\/\/+:\([0-9]*\).*$/\1/p' | head -n1)"
if [ "$api_url" = "http://$container_name:$listen_port" ]; then
  pass "ILD_API_URL is $api_url: ILD's container name and listening port"
else
  fail "ILD_API_URL='$api_url', expected http://$container_name:$listen_port"
fi

# 4. ILD runs from the image the runner service builds, from the runner target.
runner_image="$(sed -n '/^  runner:/,/^  [a-z]/s/^    image: *\(.*\)$/\1/p' "$overlay" | head -n1)"
if [ -n "$runner_image" ] && [ "$(env_value ILD_CONTAINER_RUNNER_IMAGE)" = "$runner_image" ]; then
  pass "ILD runs from $runner_image, the image the runner service builds"
else
  fail "ILD_CONTAINER_RUNNER_IMAGE='$(env_value ILD_CONTAINER_RUNNER_IMAGE)' but the runner service builds '$runner_image'"
fi
if sed -n '/^  runner:/,/^  [a-z]/p' "$overlay" | grep -q '^      target: runner$'; then
  pass "the runner service builds the Dockerfile's runner target"
else
  fail "the runner service does not build 'target: runner'"
fi

# 5. Same port inside and out, and the preview base names it.
if printf '%s\n' "$ild_section" | grep -q "^[[:space:]]*- \"$listen_port:$listen_port\""; then
  pass "ILD's port is $listen_port inside its container and outside"
else
  fail "the ild service does not publish $listen_port as $listen_port; a chat's browser and yours would disagree on preview URLs"
fi
if printf '%s\n' "$ild_section" | grep -q "ILD_PREVIEW_PROXY_BASE=\${ILD_PREVIEW_PROXY_BASE-http://[a-z.]*:$listen_port}"; then
  pass "the preview base defaults to port $listen_port"
else
  fail "ILD_PREVIEW_PROXY_BASE's default does not name port $listen_port"
fi

echo
if [ "$failures" -gt 0 ]; then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
