#!/usr/bin/env bash
# M0T7 — scripted EC1 smoke: both compose invocation forms, health assertions
# (positive + two negative cases), metrics exposition. Exits non-zero on any
# failed assertion. Owns no feature code.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

# --- Failure diagnostics -----------------------------------------------------
# On any non-zero exit, dump a sanitized snapshot of the compose project to
# artifacts/smoke-failure.log BEFORE teardown; CI's smoke job uploads
# **/*.log as the smoke-failure-diagnostics artifact. Sanitized by design:
# only the whitelisted inspect fields below -- never full inspect JSON and
# never container env (secret material). Every command is guarded with
# `|| true`, so the diagnostics path can never fail the script earlier than
# the real error and never changes its exit status.
ARTIFACT_LOG="$ROOT_DIR/artifacts/smoke-failure.log"
CURRENT_FORM="startup"
BASE_URL=""
DIAG_DONE=0
rm -f "$ARTIFACT_LOG" 2>/dev/null || true

ADHOC_CONTAINER=""
ADHOC_ENV=""

cleanup() {
    if [ -n "${ADHOC_CONTAINER:-}" ]; then
        docker rm -f "$ADHOC_CONTAINER" >/dev/null 2>&1 || true
        ADHOC_CONTAINER=""
    fi
    if [ -n "${ADHOC_ENV:-}" ] && [ -f "${ADHOC_ENV:-}" ]; then
        rm -f "$ADHOC_ENV" >/dev/null 2>&1 || true
        ADHOC_ENV=""
    fi
}

dump_diagnostics() {
    local status="$1"
    local project="${SMOKE_DIAG_PROJECT:-emailagent-dev}"
    mkdir -p "$ROOT_DIR/artifacts" 2>/dev/null || true
    {
        echo "=== smoke failure diagnostics ==="
        echo "timestamp_utc: $(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || true)"
        echo "exit_status: ${status}"
        echo "form: ${CURRENT_FORM:-unknown}"
        echo "base_url: ${BASE_URL:-unset}"
        echo "compose_project: ${project}"
        echo
        echo "--- docker compose version ---"
        docker compose version 2>&1 || true
        echo
        echo "--- config --services (root form) ---"
        docker compose -p "$project" config --services 2>&1 || true
        echo
        echo "--- config --services (-f form) ---"
        docker compose -p "$project" -f docker/docker-compose.dev.yml --env-file .env config --services 2>&1 || true
        echo
        echo "--- compose ps -a ---"
        docker compose -p "$project" ps -a 2>&1 || true
        echo
        echo "--- container state (whitelisted fields only) ---"
        local cid
        for cid in $(docker compose -p "$project" ps -aq 2>/dev/null || true); do
            docker inspect --format 'name={{.Name}} status={{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}} error={{.State.Error}} restarts={{.RestartCount}} image={{.Config.Image}} readonly_rootfs={{.HostConfig.ReadonlyRootfs}}' "$cid" 2>&1 || true
        done
        if [ -n "${ADHOC_CONTAINER:-}" ]; then
            echo "ad-hoc probe container:"
            docker inspect --format 'name={{.Name}} status={{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}} error={{.State.Error}} restarts={{.RestartCount}} image={{.Config.Image}} readonly_rootfs={{.HostConfig.ReadonlyRootfs}}' "$ADHOC_CONTAINER" 2>&1 || true
        fi
        echo
        echo "--- compose logs (tail 200) ---"
        local svc
        for svc in api postgres web; do
            echo "##### service: ${svc} #####"
            docker compose -p "$project" logs --no-color --tail=200 "$svc" 2>&1 || true
        done
    } >> "$ARTIFACT_LOG" 2>&1 || true
    echo "SMOKE FAILED (exit ${status}); diagnostics: artifacts/smoke-failure.log" >&2 || true
}

on_exit() {
    local status=$?
    # Disarm the signal traps for teardown: a second INT/TERM/HUP arriving
    # mid-dump (CI cancel escalation, double Ctrl-C) would re-enter
    # exit 130/143 and abort the diagnostics + cleanup halfway. Ignore
    # (trap - would restore the default disposition = die mid-write).
    trap '' INT TERM HUP
    set +e
    if [ "$status" -ne 0 ] && [ "$DIAG_DONE" != "1" ]; then
        DIAG_DONE=1
        dump_diagnostics "$status"
    fi
    if declare -F cleanup >/dev/null 2>&1; then
        cleanup
    fi
    exit "$status"
}
trap on_exit EXIT
# Turn termination signals into a non-zero exit so the EXIT trap (diagnostics
# + cleanup) still runs instead of the shell dying silently.
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

# Prerequisites: jq (used for JSON/network/image inspection) and GNU sort -V.
if ! command -v jq >/dev/null 2>&1; then
    echo "FAIL: 'jq' is required but not installed" >&2
    exit 1
fi

MIN_COMPOSE="2.24.0"
COMPOSE_VER="$(docker compose version --format json | sed -n 's/.*"version":"v\?\([^"]*\)".*/\1/p')"
COMPOSE_VER="${COMPOSE_VER#v}"
if [ "$(printf '%s\n%s\n' "$MIN_COMPOSE" "$COMPOSE_VER" | sort -V | head -n1)" != "$MIN_COMPOSE" ]; then
    echo "FAIL: Docker Compose $COMPOSE_VER < $MIN_COMPOSE (required for secrets source 'environment:' and 'include:')" >&2
    exit 1
fi
echo "Compose version: $COMPOSE_VER (floor $MIN_COMPOSE) OK"

# --- .env bootstrap (reuse if present; never overwrite) ---
if [ ! -f .env ]; then
    echo "Bootstrapping throwaway .env from .env.example (never committed)..."
    cp .env.example .env
    sed -i 's/^POSTGRES_PASSWORD=$/POSTGRES_PASSWORD=ci-throwaway-postgres-password/' .env
    sed -i 's/^AUTH__SigningKey=$/AUTH__SigningKey=ci-throwaway-signing-key-not-a-real-secret-32chars/' .env
    sed -i 's/^system_kek=$/system_kek=ci-throwaway-system-kek-0123456789abcdef/' .env
    # M1T1+: Audit:HmacKey is ValidateOnStart for the api service — without
    # this fill the fresh-bootstrap path dies before the first /health/ready.
    sed -i 's/^AUDIT__HmacKey=$/AUDIT__HmacKey=ci-throwaway-audit-hmac-key-0123456789abcdef/' .env
    sed -i 's|^ConnectionStrings__Postgres=$|ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=emailagent;Username=emailagent;Password=ci-throwaway-postgres-password|' .env
    # Throwaway credentials can never authenticate against an already-initialized
    # emailagent-dev cluster (POSTGRES_PASSWORD applies only at first init and
    # pg_data persists across runs), so the bootstrap path resets the pinned
    # project's volumes — mirroring the fresh CI runner this branch exists for.
    # This is destructive: gate it so a developer who simply lacks a .env is not
    # silently wiped. CI sets CI=true; otherwise opt in explicitly.
    if [ "${CI:-}" = "true" ] || [ "${SMOKE_RESET_VOLUMES:-}" = "1" ]; then
        docker compose -p emailagent-dev down -v --remove-orphans >/dev/null 2>&1 || true
    else
        echo "FAIL: .env is absent and resetting the emailagent-dev volumes requires CI=true or SMOKE_RESET_VOLUMES=1." >&2
        echo "      Either create .env (cp .env.example .env && fill values) or re-run with SMOKE_RESET_VOLUMES=1." >&2
        exit 1
    fi
fi
for k in POSTGRES_PASSWORD AUTH__SigningKey system_kek ConnectionStrings__Postgres AUDIT__HmacKey; do
    if ! grep -Eq "^${k}=.+" .env; then
        echo "FAIL: .env has no value for $k" >&2
        exit 1
    fi
    if [ "$k" = "system_kek" ]; then
        # Q-E7: System:KeyEncryptionKey is ValidateOnStart at >= 32 UTF-8
        # bytes — a shorter throwaway KEK aborts the api host at boot
        # (OptionsValidationException -> container exit before any probe).
        kek_len="$(grep -m1 '^system_kek=' .env | cut -d= -f2- | tr -d '\n' | wc -c | tr -d '[:space:]')"
        if [ "$kek_len" -lt 32 ]; then
            echo "FAIL: .env system_kek is ${kek_len} bytes; System:KeyEncryptionKey must be >= 32 bytes (Q-E7, API ValidateOnStart)" >&2
            exit 1
        fi
    fi
done

# Q-A13: the web container's nginx edge only starts when its (git-ignored)
# TLS material exists — generate the throwaway dev certificate once for this
# checkout. Probes below keep using the plain-HTTP listener (WEB_PORT); the
# HTTPS edge is exercised by scripts/smoke-m1.sh.
if [ ! -f docker/certs/dev-selfsigned.crt ] || [ ! -f docker/certs/dev-selfsigned.key ]; then
    echo "Generating dev TLS certificate (scripts/gen-dev-cert.sh)..."
    bash scripts/gen-dev-cert.sh
fi

# Pre-clean our own project (volumes persist; no -v).
docker compose -p emailagent-dev down >/dev/null 2>&1 || true

assert_contains() {
    local haystack="$1" needle="$2" label="$3"
    if ! printf '%s' "$haystack" | grep -F "$needle" >/dev/null; then
        echo "FAIL [$label]: expected body to contain '$needle'" >&2
        echo "  body: $haystack" >&2
        exit 1
    fi
}

poll_code() {
    # url expected_code deadline_seconds
    local url="$1" expected="$2" deadline="$3"
    local end=$((SECONDS + deadline)) code
    while [ "$SECONDS" -lt "$end" ]; do
        code="$(curl -s -o /dev/null -w '%{http_code}' "$url" 2>/dev/null || echo 000)"
        if [ "$code" = "$expected" ]; then
            return 0
        fi
        sleep 2
    done
    echo "FAIL: $url did not return $expected within ${deadline}s (last=$code)" >&2
    return 1
}

fetch_body() {
    curl -s "$1" 2>/dev/null || true
}

wait_postgres_healthy() {
    local end=$((SECONDS + 120)) cid status
    while [ "$SECONDS" -lt "$end" ]; do
        cid="$(docker compose -p emailagent-dev ps -q postgres 2>/dev/null || true)"
        if [ -n "$cid" ]; then
            status="$(docker inspect -f '{{.State.Health.Status}}' "$cid" 2>/dev/null || echo unknown)"
            if [ "$status" = "healthy" ]; then
                return 0
            fi
        fi
        sleep 2
    done
    echo "FAIL: postgres did not become healthy within 120s" >&2
    return 1
}

run_form() {
    local label="$1" run_neg2="$2"
    CURRENT_FORM="$label"
    echo "===== FORM: $label ====="

    # --build: this script must validate THE CURRENT CHECKOUT — without it a
    # stale local web/api image (e.g. pre-Q-A13 nginx) would be smoked
    # instead of the tree's. CI runners have no cached image, so there the
    # build happens either way.
    if [ "$label" = "root" ]; then
        docker compose -p emailagent-dev up -d --build --wait
    else
        docker compose -p emailagent-dev -f docker/docker-compose.dev.yml --env-file .env up -d --build --wait
    fi

    # M1T8: the api service is no longer host-published — probes go through
    # the web container's nginx (same-origin topology: it reverse-proxies
    # /v1/*, /health/* and /metrics, and serves the SPA on :8081). Honours
    # WEB_PORT from .env via compose's own interpolation of `ports:`.
    PORT="$(docker compose -p emailagent-dev port web 80 2>/dev/null | sed -n 's/.*://p')"
    if [ -z "$PORT" ]; then
        PORT=8081
    fi
    BASE_URL="http://localhost:${PORT}"

    # Positive: live
    poll_code "${BASE_URL}/health/live" 200 120
    echo "  /health/live -> 200 OK"

    # Positive: web payload (SPA artifact actually served, not just nginx's static 200)
    if ! docker compose -p emailagent-dev exec -T web wget -qO- http://localhost/ 2>/dev/null | grep -i '<html' >/dev/null; then
        echo "FAIL [web]: SPA index payload not served by web container" >&2
        exit 1
    fi
    echo "  web / -> SPA index payload OK"

    # Positive: ready
    poll_code "${BASE_URL}/health/ready" 200 120
    local body
    body="$(fetch_body "${BASE_URL}/health/ready")"
    assert_contains "$body" '"db":"Healthy"' "ready db healthy"
    assert_contains "$body" '"blob-volume":"Healthy"' "ready blob healthy"
    echo "  /health/ready -> 200 ($body)"

    # Positive: metrics
    poll_code "${BASE_URL}/metrics" 200 60
    local mbody
    mbody="$(fetch_body "${BASE_URL}/metrics")"
    if ! printf '%s' "$mbody" | grep -E '# HELP|# TYPE' >/dev/null; then
        echo "FAIL [metrics]: no Prometheus exposition markers" >&2
        printf '%s\n' "$mbody" | head -20 >&2
        exit 1
    fi
    echo "  /metrics -> 200 (Prometheus exposition present)"

    # Negative case 1: stop postgres
    echo "  -- negative case 1: stop postgres --"
    docker compose -p emailagent-dev stop postgres
    local neg_end=$((SECONDS + 120)) code body2
    while [ "$SECONDS" -lt "$neg_end" ]; do
        code="$(curl -s -o /tmp/ea_ready_body -w '%{http_code}' "${BASE_URL}/health/ready" 2>/dev/null || echo 000)"
        body2="$(cat /tmp/ea_ready_body 2>/dev/null || true)"
        if [ "$code" = "503" ] && printf '%s' "$body2" | grep -F '"db":"Unhealthy"' >/dev/null; then
            break
        fi
        sleep 2
    done
    if [ "${code:-}" != "503" ] || ! printf '%s' "${body2:-}" | grep -F '"db":"Unhealthy"' >/dev/null; then
        echo "FAIL [neg1]: expected 503 with db Unhealthy (got ${code:-}: ${body2:-})" >&2
        exit 1
    fi
    echo "  /health/ready -> 503 (db Unhealthy) OK"

    docker compose -p emailagent-dev start postgres
    wait_postgres_healthy
    poll_code "${BASE_URL}/health/ready" 200 120
    body="$(fetch_body "${BASE_URL}/health/ready")"
    assert_contains "$body" '"db":"Healthy"' "ready db healthy after restart"
    assert_contains "$body" '"blob-volume":"Healthy"' "ready blob healthy after restart"
    echo "  /health/ready -> 200 after postgres restart OK"

    # Negative case 2 (run once)
    if [ "$run_neg2" = "yes" ]; then
        echo "  -- negative case 2: ad-hoc container with unwritable Blobs__Path --"
        local api_cid api_image api_net conn_str
        api_cid="$(docker compose -p emailagent-dev ps -q api)"
        if [ -z "$api_cid" ]; then
            echo "FAIL: cannot find api container" >&2
            exit 1
        fi
        api_image="$(docker inspect "$api_cid" | jq -r '.[0].Config.Image')"
        api_net="$(docker inspect "$api_cid" | jq -r '.[0].NetworkSettings.Networks | keys[0]')"
        # M1T1: the api service's connection string is materialized inside the
        # container by docker/entrypoint.sh from the postgres Docker secret, so
        # it no longer exists in the container's Config.Env. Derive the ad-hoc
        # probe's connection string from the same secret; fall back to
        # Config.Env (pre-M1T1 images) and then to .env. The secret-derived
        # value always matches the running server, so a .env connection-string
        # line drifting from POSTGRES_PASSWORD cannot cause a false failure.
        local pg_password=""
        pg_password="$(docker compose -p emailagent-dev exec -T postgres sh -c 'cat /run/secrets/postgres_password' 2>/dev/null | tr -d '\r\n' || true)"
        if [ -n "$pg_password" ]; then
            conn_str="Host=postgres;Port=5432;Database=emailagent;Username=emailagent;Password=${pg_password}"
        else
            echo "WARN: could not read the postgres secret; falling back to Config.Env/.env for the ad-hoc probe connection string" >&2
            conn_str="$(docker inspect "$api_cid" | jq -r '.[0].Config.Env[]' | sed -n 's/^ConnectionStrings__Postgres=//p')"
            if [ -z "$conn_str" ]; then
                conn_str="$(sed -n 's/^ConnectionStrings__Postgres=//p' .env)"
            fi
        fi

        ADHOC_ENV="$(mktemp)"
        chmod 600 "$ADHOC_ENV"
        # The ad-hoc container runs the image's plain CMD (no compose-level
        # entrypoint), so docker/entrypoint.sh never materializes the M1
        # secrets and the app's ValidateOnStart validators (System:KeyEncryption
        # / Audit:HmacKey / Auth:SigningKey — M1T3/M1T4) would kill it before
        # the probe. Feed them from .env (throwaway dev values, file mode 600,
        # container removed below) exactly as the compose `api` service gets
        # them. Pre-existing M1T4 drift in this M0-era smoke, surfaced by the
        # Q-A13 run of this script.
        printf 'ConnectionStrings__Postgres=%s\nBlobs__Path=/root/locked\n' "$conn_str" > "$ADHOC_ENV"
        printf 'AUDIT__HmacKey=%s\n' "$(grep '^AUDIT__HmacKey=' .env | cut -d= -f2-)" >> "$ADHOC_ENV"
        printf 'AUTH__SigningKey=%s\n' "$(grep '^AUTH__SigningKey=' .env | cut -d= -f2-)" >> "$ADHOC_ENV"
        printf 'SYSTEM__KeyEncryptionKey=%s\n' "$(grep '^system_kek=' .env | cut -d= -f2-)" >> "$ADHOC_ENV"

        ADHOC_CONTAINER="$(docker run -d --network "$api_net" --env-file "$ADHOC_ENV" "$api_image")"

        local adhoc_end=$((SECONDS + 60)) final acode abody
        while [ "$SECONDS" -lt "$adhoc_end" ]; do
            final="$(docker exec "$ADHOC_CONTAINER" sh -c 'code=$(curl -s -o /tmp/b -w "%{http_code}" http://localhost:8080/health/ready); printf "%s\n" "$code"; cat /tmp/b' 2>/dev/null || true)"
            acode="$(printf '%s' "$final" | head -n1)"
            abody="$(printf '%s' "$final" | tail -n +2)"
            if [ "$acode" = "503" ] && printf '%s' "$abody" | grep -F '"blob-volume":"Unhealthy"' >/dev/null && printf '%s' "$abody" | grep -F '"db":"Healthy"' >/dev/null; then
                break
            fi
            sleep 2
        done

        final="$(docker exec "$ADHOC_CONTAINER" sh -c 'code=$(curl -s -o /tmp/b -w "%{http_code}" http://localhost:8080/health/ready); printf "%s\n" "$code"; cat /tmp/b' 2>/dev/null || true)"
        acode="$(printf '%s' "$final" | head -n1)"
        abody="$(printf '%s' "$final" | tail -n +2)"
        echo "  ad-hoc: HTTP ${acode} body=${abody}"

        if [ "$acode" != "503" ]; then
            echo "FAIL [neg2]: expected 503 (got $acode)" >&2
            exit 1
        fi
        if ! printf '%s' "$abody" | grep -F '"blob-volume":"Unhealthy"' >/dev/null; then
            echo "FAIL [neg2]: expected blob-volume Unhealthy" >&2
            exit 1
        fi
        if ! printf '%s' "$abody" | grep -F '"db":"Healthy"' >/dev/null; then
            echo "FAIL [neg2]: expected db Healthy" >&2
            exit 1
        fi
        echo "  ad-hoc -> 503 (blob-volume Unhealthy, db Healthy) OK"

        docker rm -f "$ADHOC_CONTAINER" >/dev/null 2>&1 || true
        ADHOC_CONTAINER=""
        if [ -n "${ADHOC_ENV:-}" ] && [ -f "${ADHOC_ENV:-}" ]; then
            rm -f "$ADHOC_ENV" >/dev/null 2>&1 || true
            ADHOC_ENV=""
        fi
    fi

    # Teardown
    echo "  -- teardown --"
    docker compose -p emailagent-dev down
    echo "===== FORM $label DONE ====="
}

run_form "root" "yes"
run_form "f-flag" "no"

echo "SMOKE PASSED: both forms, all positive + negative assertions green."
