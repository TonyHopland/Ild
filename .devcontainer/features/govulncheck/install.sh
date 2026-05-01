#!/bin/bash
set -e

echo "Installing govulncheck..."

# Install govulncheck using go install
go install golang.org/x/vuln/cmd/govulncheck@latest

echo "govulncheck installed successfully"
