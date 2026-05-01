#!/bin/bash
set -e

echo "Installing OpenCode for user $_REMOTE_USER..."

# Run the installer as the primary user
su - $_REMOTE_USER -c "curl -fsSL https://opencode.ai/install | bash"

echo "OpenCode installed successfully"
