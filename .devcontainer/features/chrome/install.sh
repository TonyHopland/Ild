#!/bin/bash
set -e

export DEBIAN_FRONTEND=noninteractive

# Google publishes google-chrome-stable for amd64 and arm64 Linux only, so pick
# the .deb matching the build architecture and skip (rather than fail the
# container build) on anything else — same semantics as the WITH_CHROME step in
# the root Dockerfile.
arch="$(dpkg --print-architecture)"
case "$arch" in
  amd64 | arm64) ;;
  *)
    echo "Skipping Google Chrome: no upstream package for $arch"
    exit 0
    ;;
esac

echo "Installing Google Chrome ($arch)..."

chrome_deb="/tmp/google-chrome-stable_current_${arch}.deb"

apt-get update
wget -q -O "$chrome_deb" "https://dl.google.com/linux/direct/google-chrome-stable_current_${arch}.deb"
apt-get install -y "$chrome_deb"

rm -f "$chrome_deb"

echo "Google Chrome installed successfully"
