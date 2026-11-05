#!/bin/sh
# M1T1 — secret materialization for the api container (M1T1 task step 8).
#
# Docker Compose mounts each secret at /run/secrets/<name>. The application
# reads the plain environment variables named in §16, and Npgsql does not
# support `Password_file=` indirection, so the values are exported here
# BEFORE the process starts. (The `*_FILE` indirection used by the Postgres
# service is Postgres-image-specific and does not apply to the API.)
#
# Invoked by docker/docker-compose.dev.yml as:
#   entrypoint: ["/bin/sh", "/entrypoint.sh"]
# — via /bin/sh so the file does not need the executable bit.
set -eu

# Plain assignments first: unlike `export VAR=$(...)`, a failed command
# substitution in an assignment propagates to `set -e`.
AUTH__SigningKey="$(cat /run/secrets/AUTH__SigningKey)"
export AUTH__SigningKey

AUDIT__HmacKey="$(cat /run/secrets/AUDIT__HmacKey)"
export AUDIT__HmacKey

# Secret file name keeps M0's `system_kek` (§17 dev-secrets list); the
# application-facing variable is SYSTEM__KeyEncryptionKey (§16 / Q-E7).
SYSTEM__KeyEncryptionKey="$(cat /run/secrets/system_kek)"
export SYSTEM__KeyEncryptionKey

CONNECTIONSTRINGS__POSTGRES__PASSWORD="$(cat /run/secrets/postgres_password)"
export CONNECTIONSTRINGS__POSTGRES__PASSWORD

# Fail fast on values that would make the composed connection string behave
# differently from the intended password: Npgsql trims surrounding
# whitespace, and a ';' terminates the value while quotes start Npgsql's own
# quoting — better a clear crash-loop message than a silently unhealthy
# /health/ready.
case "${CONNECTIONSTRINGS__POSTGRES__PASSWORD}" in
    "" | [[:space:]]* | *[[:space:]])
        echo "entrypoint: postgres password must not be empty or start/end with whitespace;" >&2
        echo "            Npgsql connection-string parsing trims surrounding whitespace." >&2
        exit 1
        ;;
    *";"* | *"'"* | *'"'*)
        echo "entrypoint: postgres password must not contain semicolons or single/double quotes;" >&2
        echo "            it cannot be embedded safely in ConnectionStrings__Postgres (Npgsql)." >&2
        echo "            Use a password without these characters (see .env.example)." >&2
        exit 1
        ;;
esac

# Full connection string composed from the materialized secret (§17: the
# connection string uses the env var, no `_file` suffix).
ConnectionStrings__Postgres="Host=postgres;Port=5432;Database=emailagent;Username=emailagent;Password=${CONNECTIONSTRINGS__POSTGRES__PASSWORD}"
export ConnectionStrings__Postgres

exec dotnet EmailAgent.Api.dll
