#!/bin/bash
set -e

echo "Installing Google Chrome..."
wget -qO- https://dl-ssl.google.com/linux/linux_signing_key.pub | gpg --dearmor -o /usr/share/keyrings/google-chrome.gpg
echo "deb [signed-by=/usr/share/keyrings/google-chrome.gpg] http://dl.google.com/linux/chrome/deb/ stable main" >> /etc/apt/sources.list.d/google.list

apt-get update
apt-get install -y google-chrome-stable
apt-get clean
rm -rf /var/lib/apt/lists/*

echo "Google Chrome installed successfully"
