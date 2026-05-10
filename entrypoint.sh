#!/bin/bash
set -e

CERTS_DIR="/etc/extra-certs"
CA_CERTS_DIR="/usr/local/share/ca-certificates"

if [ -d "$CERTS_DIR" ]; then
  copied=0
  for cert in "$CERTS_DIR"/*.crt "$CERTS_DIR"/*.pem; do
    [ -e "$cert" ] || continue
    cp "$cert" "$CA_CERTS_DIR/"
    copied=1
  done
  if [ "$copied" -eq 1 ]; then
    update-ca-certificates
  fi
fi

exec "$@"
