#!/usr/bin/env bash
#
# Tests for the container-run wiring in docker-compose.yml: ILD runs each loop run, chat and provider sign-in in a
# container of its own, on the engine whose socket it is given.
#
# What the file promises, each of which fails at run time with nothing on screen if it drifts:
#   1. It refuses to start without ILD_CONTAINER_SOCKET_HOST, saying where to set it: the socket is a trust
#      decision ILD never makes for the operator.
#   2. ILD names its own container as the ild service's container_name, or it cannot join a run's network.
#   3. ILD_API_URL names ILD by that container name, which each run's container is given ILD's address for.
#   4. ILD runs from the image the runner service builds, and that service builds the runner target.
#   5. ILD's port is the same inside its container and outside, and the preview base names that port: a preview
#      URL is opened by a browser outside and by a chat's browser inside.
#
# Input: `docker compose config` where the docker CLI is available, with dummies for the file's other required
# variables; otherwise the file is scanned raw, and that is said.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
compose="$repo_root/docker-compose.yml"

failures=0
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
pass() { echo "ok: $*"; }

# 1. The socket is required, by name and with a message.
if grep -q '\${ILD_CONTAINER_SOCKET_HOST:?[^}]*\.env' "$compose"; then
  pass "compose refuses to start without ILD_CONTAINER_SOCKET_HOST and says to set it in .env"
else
  fail "the socket mount is not '\${ILD_CONTAINER_SOCKET_HOST:?...set it in .env...}'"
fi

dummies() {
  {
    grep -o '\${[A-Za-z_][A-Za-z0-9_]*:?' "$compose" | sed 's/^.*\${//; s/:?$//'
    grep -o '\${[A-Za-z_][A-Za-z0-9_]*}' "$compose" | sed 's/^.*\${//; s/}$//'
  } | sort -u | grep -v -x ILD_CONTAINER_SOCKET_HOST | sed 's/$/=ci-dummy/'
}

model=""
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  resolved="$(mktemp)"; errors="$(mktemp)"
  trap 'rm -f "$resolved" "$errors"' EXIT
  # shellcheck disable=SC2046  # word splitting is how the assignments are passed
  if (cd "$repo_root" && env -u ILD_CONTAINER_SOCKET_HOST $(dummies) \
        docker compose -f "$compose" config) >/dev/null 2>"$errors"; then
    fail "docker compose config resolved the file with no ILD_CONTAINER_SOCKET_HOST"
  elif grep -q 'ILD_CONTAINER_SOCKET_HOST' "$errors"; then
    pass "without the socket, compose stops and names ILD_CONTAINER_SOCKET_HOST"
  else
    fail "without the socket, compose failed for another reason: $(cat "$errors")"
  fi
  # shellcheck disable=SC2046
  if ! (cd "$repo_root" && env $(dummies) ILD_CONTAINER_SOCKET_HOST=/run/podman/podman.sock \
        docker compose -f "$compose" config) >"$resolved" 2>"$errors"; then
    echo "FAIL: docker compose config could not resolve docker-compose.yml:"; cat "$errors"; exit 1
  fi
  model="$resolved"
  echo "input: docker compose config over docker-compose.yml"
else
  model="$compose"
  echo "input: docker-compose.yml scanned raw (docker CLI unavailable here)"
fi

# The value of an environment entry, in either the list form the file uses or the map form compose config prints.
env_value() { # <name>
  sed -n -e "s/^[[:space:]]*- $1=\(.*\)$/\1/p" -e "s/^[[:space:]]*$1: \"\{0,1\}\([^\"]*\)\"\{0,1\}$/\1/p" "$model" | tail -n1
}

# 2. ILD names its own container as the ild service names it.
ild_section="$(sed -n '/^  ild:/,/^  [a-z]/p' "$compose")"
container_name="$(printf '%s\n' "$ild_section" | sed -n 's/^    container_name: *\(.*\)$/\1/p' | head -n1)"
named="$(env_value ILD_CONTAINER_ILD_CONTAINER)"
if [ -n "$container_name" ] && [ "$named" = "$container_name" ]; then
  pass "ILD_CONTAINER_ILD_CONTAINER is the ild service's container_name ($container_name)"
else
  fail "ILD_CONTAINER_ILD_CONTAINER='$named' but the ild service's container_name is '$container_name'"
fi

# 3. ILD_API_URL names ILD by that container name, on the port ILD listens on. The raw file has the default inside
# ${ILD_API_URL:-...}; compose config has the value.
api_url="$(env_value ILD_API_URL | sed 's/^\${ILD_API_URL:-\(.*\)}$/\1/')"
listen_port="$(printf '%s\n' "$ild_section" | sed -n 's/^[[:space:]]*- ASPNETCORE_URLS=http:\/\/+:\([0-9]*\).*$/\1/p' | head -n1)"
if [ "$api_url" = "http://$container_name:$listen_port" ]; then
  pass "ILD_API_URL is $api_url: ILD's container name and listening port"
else
  fail "ILD_API_URL='$api_url', expected http://$container_name:$listen_port"
fi

# 4. ILD runs from the image the runner service builds, from the runner target.
runner_section="$(sed -n '/^  runner:/,/^  [a-z]/p' "$compose")"
runner_image="$(printf '%s\n' "$runner_section" | sed -n 's/^    image: *\(.*\)$/\1/p' | head -n1)"
image="$(env_value ILD_CONTAINER_RUNNER_IMAGE | sed 's/^\${ILD_CONTAINER_RUNNER_IMAGE:-\(.*\)}$/\1/')"
if [ -n "$runner_image" ] && [ "$image" = "$runner_image" ]; then
  pass "ILD runs from $runner_image, the image the runner service builds"
else
  fail "ILD_CONTAINER_RUNNER_IMAGE='$image' but the runner service builds '$runner_image'"
fi
if printf '%s\n' "$runner_section" | grep -q '^      target: runner$'; then
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

# 6. A fresh copy of .env.example leaves the socket empty, so the guard above fires instead of mounting a default.
if grep -q '^ILD_CONTAINER_SOCKET_HOST=$' "$repo_root/.env.example" && ! grep -q '^ILD_CONTAINER_SOCKET_HOST=.' "$repo_root/.env.example"; then
  pass ".env.example leaves ILD_CONTAINER_SOCKET_HOST empty"
else
  fail ".env.example does not have an uncommented, empty ILD_CONTAINER_SOCKET_HOST="
fi

# 7. The dedicated gid reaches the container.
if grep -q 'ILD_CONTAINER_SOCKET_GID=\${ILD_CONTAINER_SOCKET_GID:-10099}' "$compose"; then
  pass "compose passes ILD_CONTAINER_SOCKET_GID (default 10099)"
else
  fail "compose does not pass ILD_CONTAINER_SOCKET_GID"
fi

echo
if [ "$failures" -gt 0 ]; then echo "$failures failure(s)"; exit 1; fi
echo "all passed"
