#!/bin/bash
set -e

echo "Installing Google Chrome..."

export DEBIAN_FRONTEND=noninteractive

chrome_deb="/tmp/google-chrome-stable_current_amd64.deb"

apt-get update
wget -q -O "$chrome_deb" https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb
apt-get install -y "$chrome_deb"

rm -f "$chrome_deb"

echo "Google Chrome installed successfully"
