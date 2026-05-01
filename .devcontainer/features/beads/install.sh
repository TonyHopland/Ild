#!/bin/bash
set -e

echo "Installing Beads..."

# Run the installer as the primary user
curl -fsSL https://raw.githubusercontent.com/steveyegge/beads/main/scripts/install.sh | bash

echo "Beads installed successfully"
