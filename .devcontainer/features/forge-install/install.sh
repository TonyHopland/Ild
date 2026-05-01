#!/bin/bash
set -e

echo "Installing Forge for user $_REMOTE_USER..."

# Run the installer as the primary user
su - $_REMOTE_USER -c "curl -fsSL https://raw.githubusercontent.com/Robin831/Forge/main/install.sh | bash"

echo "Forge installed successfully"
