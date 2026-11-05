#!/usr/bin/env bash
# Q-A13 — throwaway self-signed TLS certificate for the dev edge.
#
# The dev nginx front-end (docker/nginx.conf) serves HTTPS on :443 from
# docker/certs/dev-selfsigned.{crt,key} to simulate the prod/testing posture
# where traefik terminates TLS externally: browsers get a real HTTPS origin
# in dev, so the Secure ea_refresh cookie flows like it does in prod.
#
# The certificate is DEV-ONLY: self-signed, no passphrase, never committed
# (docker/certs/ is git-ignored) and carries no secret value. Browsers and
# curl will not trust it — use `curl -k` or accept the warning once.
#
# Idempotent: exits 0 without touching an existing pair; pass --force (or
# -f) to regenerate. Portable (paths resolved relative to this script,
# Linux target — AGENTS.md #1).
#
# Usage: bash scripts/gen-dev-cert.sh [--force]
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CERT_DIR="${ROOT_DIR}/docker/certs"
CRT="${CERT_DIR}/dev-selfsigned.crt"
KEY="${CERT_DIR}/dev-selfsigned.key"
DAYS=825 # browser cap for locally-installed certs; long enough to not nag

log() { printf '[gen-dev-cert] %s\n' "$*"; }

command -v openssl >/dev/null 2>&1 || {
    printf '[gen-dev-cert] FAIL: openssl not found\n' >&2
    exit 1
}

force=false
case "${1:-}" in
    "" ) ;;
    --force | -f ) force=true ;;
    * )
        printf 'usage: bash scripts/gen-dev-cert.sh [--force]\n' >&2
        exit 2
        ;;
esac

if [ "$force" = false ] && [ -f "$CRT" ] && [ -f "$KEY" ]; then
    log "exists, keeping docker/certs (use --force to regenerate)"
    exit 0
fi

mkdir -p "$CERT_DIR"

# SAN covers how the dev edge is reached from the host: https://localhost
# (nginx serves the Host the browser sent, so SNI/identity is localhost).
# RSA-2048 + SHA-256: accepted everywhere, no passphrase (-nodes) — a
# throwaway dev certificate is no secret to leak (AGENTS.md #6).
# -addext needs OpenSSL >= 1.1.1 (2018; present on any current Linux and in
# the docker:27 / alpine images the smoke scripts use).
log "generating self-signed certificate (SAN DNS:localhost,IP:127.0.0.1, ${DAYS} days)"
if ! openssl req -x509 -newkey rsa:2048 -sha256 -days "$DAYS" -nodes \
    -keyout "$KEY" \
    -out "$CRT" \
    -subj "/CN=localhost" \
    -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
then
    printf '[gen-dev-cert] FAIL: openssl req failed (needs OpenSSL >= 1.1.1 for -addext)\n' >&2
    rm -f "$CRT" "$KEY"
    exit 1
fi

# The key is dev material but keep it owner-only anyway.
chmod 600 "$KEY"
chmod 644 "$CRT"

log "wrote docker/certs/dev-selfsigned.crt + .key"
log "trust is NOT configured — browsers warn, curl needs -k"
