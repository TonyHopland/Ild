#!/usr/bin/env bash
#
# Compute the MSBuild informational Version stamped into the published images
# (see docs/adr/0012-ghcr-image-tagging-strategy.md).
#
#   release tag vX.Y.Z -> X.Y.Z           (the bare release version)
#   push to main       -> <base>-main+<shortsha>
#
# <base> is read from <Version> in Directory.Build.props. Only the informational
# Version is overridden; the numeric AssemblyVersion/FileVersion stay as the
# props define them, so main builds keep clean numeric assembly versions.
#
# Usage: compute-version.sh <ref-type> <ref-name> <short-sha> [props-file]
set -euo pipefail

ref_type="${1:?usage: compute-version.sh <ref-type> <ref-name> <short-sha> [props-file]}"
ref_name="${2:?ref-name required}"
short_sha="${3:?short-sha required}"
props_file="${4:-Directory.Build.props}"

case "$ref_type" in
  tag)
    if [[ ! "$ref_name" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([-+.].*)?$ ]]; then
      echo "compute-version: '$ref_name' is not a canonical vX.Y.Z release tag" >&2
      exit 1
    fi
    echo "${ref_name#v}"
    ;;
  branch)
    base="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$props_file" | head -n1)"
    if [[ -z "$base" ]]; then
      echo "compute-version: could not read <Version> from $props_file" >&2
      exit 1
    fi
    echo "${base}-${ref_name}+${short_sha}"
    ;;
  *)
    echo "compute-version: unsupported ref type '$ref_type'" >&2
    exit 1
    ;;
esac
