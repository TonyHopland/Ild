#!/usr/bin/env bash
#
# Compute the MSBuild informational Version stamped into the published images
# (see docs/adr/0012-ghcr-image-tagging-strategy.md). Publishing runs only on
# release tags:
#
#   release tag vX.Y.Z -> X.Y.Z           (the bare release version)
#
# Only the informational Version is overridden; the numeric AssemblyVersion/
# FileVersion stay as Directory.Build.props defines them, so assembly identity
# stays clean and only the human-facing version carries the release version.
#
# Usage: compute-version.sh <ref-type> <ref-name>
set -euo pipefail

ref_type="${1:?usage: compute-version.sh <ref-type> <ref-name>}"
ref_name="${2:?ref-name required}"

case "$ref_type" in
  tag)
    if [[ ! "$ref_name" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([-+.].*)?$ ]]; then
      echo "compute-version: '$ref_name' is not a canonical vX.Y.Z release tag" >&2
      exit 1
    fi
    echo "${ref_name#v}"
    ;;
  *)
    echo "compute-version: unsupported ref type '$ref_type' (publishing is release-tag only)" >&2
    exit 1
    ;;
esac
