#!/bin/bash
set -e

echo "Installing Dolt..."

# Install Dolt to /usr/local/bin (system-wide, accessible to all users)
curl -L https://github.com/dolthub/dolt/releases/latest/download/install.sh | DOLT_INSTALL_DIR=/usr/local/bin bash

echo "Dolt installed successfully"
