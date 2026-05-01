#!/bin/bash
set -e

echo "Installing Claude Code for user $_REMOTE_USER..."

# Run the installer as the primary user to install in their home directory
su - $_REMOTE_USER -c "curl -fsSL https://claude.ai/install.sh | bash"

echo "Claude Code installed successfully"
