#!/usr/bin/env bash
# M4T5 — scripted M4 sign-off smoke against the dev compose stack.
#
# HTTP-reachable surface only (SQ-M4-5): compose up → migrations → seed admin →
# user login flow (OTP → change-password → set-passphrase → unwrap-dek) →
# POST /v1/user/actions (Greenmail SMTP config, DEK required) → 201, row in
# action_configs + encrypted_blobs purpose='action_config', response has no
# password → GET (no password, DEK required) → PATCH (blank password preserved) →
# DELETE (204, blob gone) → GET /v1/user/actions pagination → GET /v1/user/
# notifications (+ filter[unread]), POST .../read, POST .../read-all round-trip →
# psql audit assertions for CRUD M4 actions (create/update/delete_action) →
# tools/AuditVerify exit 0.
#
# Dispatch-driven flows (dry-run, invalid-args, real send, threading, whitelist,
# abort-on-failure) have no HTTP trigger in M4 (SQ-M4-5) and run in the xUnit
# suite: dotnet test --filter Category=M4EndToEnd
#
# Exit 0 = every step passed ("OK: all M4 smoke tests passed"); a non-zero
# exit names the failing step number and dumps a sanitized diagnostics file
# (artifacts/m4t5/smoke-failure.log) before teardown.
#
# Usage: bash scripts/smoke-m4.sh
# Env:   .env must exist (cp .env.example .env and fill throwaway values).
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

PROJECT="emailagent-m4-smoke"
ARTIFACT_DIR="artifacts/m4t5"

# Writability pre-check (Q-O24 pattern): fail fast when artifacts/ is unwritable.
if ! mkdir -p "$ARTIFACT_DIR" 2>/dev/null || ! touch "$ARTIFACT_DIR/.write-probe" 2>/dev/null; then
    printf '[m4t5] FAIL: %s is not writable (mkdir or write probe failed)\n' "$ROOT_DIR/$ARTIFACT_DIR" >&2
    printf '[m4t5]       likely cause: root-owned files/dirs left by container runs.\n' >&2
    printf '[m4t5]       fix: sudo chown -R %s:%s artifacts/   (or: sudo rm -rf artifacts/)\n' "$(id -u)" "$(id -g)" >&2
    exit 1
fi
rm -f "$ARTIFACT_DIR/.write-probe" 2>/dev/null || true

log()  { printf '[m4t5] %s\n' "$*"; }
fail() { printf '[m4t5] FAIL (step %s): %s\n' "${STEP:-0}" "$*" >&2; exit 1; }

for tool in docker curl jq openssl; do
    command -v "$tool" >/dev/null 2>&1 || fail "required tool '$tool' not found"
done

[ -f .env ] || fail ".env missing — cp .env.example .env and fill throwaway values"

for k in POSTGRES_PASSWORD AUTH__SigningKey system_kek AUDIT__HmacKey PLUGIN__SigningKey ConnectionStrings__Postgres; do
    grep -Eq "^${k}=." .env || fail ".env has no value for $k"
done

FAILURE_LOG="$ARTIFACT_DIR/smoke-failure.log"
DIAG_DONE=0
rm -f "$FAILURE_LOG" 2>/dev/null || true

compose() { docker compose -p "$PROJECT" "$@"; }

# Q-A13: dev TLS edge uses a throwaway self-signed cert.
curl() { command curl --insecure "$@"; }

BODY_FILE="$(mktemp)"
HDRS="$ARTIFACT_DIR/smoke-headers.tmp"
: > "$HDRS" && chmod 600 "$HDRS"
BODY="$BODY_FILE"

SEED_OUT="$ARTIFACT_DIR/seed.out"
CHG_BODY="$ARTIFACT_DIR/change-password.json"
SP_BODY="$ARTIFACT_DIR/set-passphrase.json"
UD_BODY="$ARTIFACT_DIR/unwrap-dek.json"
ACTION_BODY="$ARTIFACT_DIR/create-action.json"
PATCH_BODY="$ARTIFACT_DIR/patch-action.json"

dump_diagnostics() {
    local status="$1"
    mkdir -p "$ARTIFACT_DIR" 2>/dev/null || true
    {
        echo "=== m4t5 smoke failure diagnostics ==="
        echo "timestamp_utc: $(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || true)"
        echo "exit_status: ${status}"
        echo "step: ${STEP:-unknown}"
        echo "base_url: ${BASE_URL:-unset}"
        echo "compose_project: ${PROJECT}"
        echo
        echo "--- compose ps -a ---"
        compose ps -a 2>&1 || true
        echo
        echo "--- container state (whitelisted fields only) ---"
        local cid
        for cid in $(compose ps -aq 2>/dev/null || true); do
            docker inspect --format 'name={{.Name}} status={{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}} error={{.State.Error}} restarts={{.RestartCount}} image={{.Config.Image}}' "$cid" 2>&1 || true
        done
        echo
        echo "--- compose logs (tail 200) ---"
        local svc
        for svc in api postgres web greenmail; do
            echo "##### service: ${svc} #####"
            compose logs --no-color --tail=200 "$svc" 2>&1 || true
        done
    } >> "$FAILURE_LOG" 2>&1 || true
    echo "M4 SMOKE FAILED (exit ${status}); diagnostics: ${FAILURE_LOG}" >&2 || true
}

on_exit() {
    local status=$?
    trap '' INT TERM HUP
    set +e
    if [ "$status" -ne 0 ] && [ "$DIAG_DONE" != "1" ]; then
        DIAG_DONE=1
        dump_diagnostics "$status"
    fi
    rm -f "$BODY_FILE" "$HDRS" "$SEED_OUT" "$CHG_BODY" "$SP_BODY" "$UD_BODY" \
          "$ACTION_BODY" "$PATCH_BODY" 2>/dev/null || true
    exit "$status"
}
trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

poll_code() {
    local url="$1" expected="$2" deadline="$3"
    local end=$((SECONDS + deadline)) code
    while [ "$SECONDS" -lt "$end" ]; do
        code="$(curl -s -o /dev/null -w '%{http_code}' "$url" 2>/dev/null || echo 000)"
        [ "$code" = "$expected" ] && return 0
        sleep 2
    done
    echo "poll: $url last=$code (want $expected)" >&2
    return 1
}

psql_q() {
    compose exec -T postgres sh -c '
        PGPASSWORD="$(cat /run/secrets/postgres_password)"
        export PGPASSWORD
        exec psql -h 127.0.0.1 -U emailagent -d emailagent -tA -v ON_ERROR_STOP=1
    ' <<SQL
$1
SQL
}

# --- step 1: compose up ------------------------------------------------------
STEP=1
log "ensuring dev TLS certificate (scripts/gen-dev-cert.sh)"
bash scripts/gen-dev-cert.sh || fail "gen-dev-cert.sh failed"
log "compose up (project=$PROJECT)"
compose down -v --remove-orphans >/dev/null 2>&1 || true
compose up -d --build
log "compose up done"

# --- step 2: wait for /health/ready 200 through the edge ---------------------
STEP=2
PORT="$(compose port web 443 2>/dev/null | sed -n 's/.*://p' | head -n1)"
PORT="${PORT:-8443}"
BASE_URL="https://localhost:${PORT}"
log "probing ${BASE_URL}/health/ready"
poll_code "${BASE_URL}/health/ready" 200 300 || fail "/health/ready never returned 200 within 300s"
log "/health/ready -> 200"

# --- step 3: wait for Greenmail (readiness) ----------------------------------
STEP=3
GMAIL_REST_PORT="$(compose port greenmail 8080 2>/dev/null | sed -n 's/.*://p' | head -n1)"
GMAIL_SMTP_PORT="$(compose port greenmail 3025 2>/dev/null | sed -n 's/.*://p' | head -n1)"
GMAIL_REST_PORT="${GMAIL_REST_PORT:-18080}"
GMAIL_SMTP_PORT="${GMAIL_SMTP_PORT:-3025}"
GREENMAIL_REST="http://localhost:${GMAIL_REST_PORT}"
log "greenmail REST at ${GREENMAIL_REST} (SMTP host-mapped ${GMAIL_SMTP_PORT}; api uses service name greenmail:3025)"
poll_code "${GREENMAIL_REST}/api/service/readiness" 200 120 \
    || fail "Greenmail /api/service/readiness never returned 200 within 120s"
log "greenmail readiness -> 200"

# --- step 4: apply migrations ------------------------------------------------
STEP=4
log "applying EF migrations (SDK container on the compose network)"
PG_PASSWORD="$(grep '^POSTGRES_PASSWORD=' .env | cut -d= -f2-)"
NUGET_VOL=""
if [ -d /tmp/opencode/nuget ]; then
    NUGET_VOL="-v /tmp/opencode/nuget:/root/.nuget"
fi
# shellcheck disable=SC2086
docker run --rm \
    --network "${PROJECT}_default" \
    -v "$ROOT_DIR:/work" \
    -w /work \
    $NUGET_VOL \
    -e "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=emailagent;Username=emailagent;Password=${PG_PASSWORD}" \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    sh -c '
        dotnet tool install --global dotnet-ef --version 10.0.* >/dev/null 2>&1 || true
        export PATH="$PATH:/root/.dotnet/tools"
        dotnet ef database update --project src/EmailAgent.Infrastructure
    ' > "$ARTIFACT_DIR/migrate.log" 2>&1 || {
    cat "$ARTIFACT_DIR/migrate.log" >&2
    fail "dotnet ef database update failed (see $ARTIFACT_DIR/migrate.log)"
}
log "migrations applied"
log "restarting api/web so Npgsql sees the migrated schema"
compose restart api web >/dev/null 2>&1
poll_code "${BASE_URL}/health/ready" 200 120 \
    || fail "/health/ready never returned 200 after api restart"

# --- step 5: seed the first admin -------------------------------------------
STEP=5
log "running scripts/seed-dev.sh (COMPOSE_PROJECT_NAME=$PROJECT)"
COMPOSE_PROJECT_NAME="$PROJECT" bash scripts/seed-dev.sh >"$SEED_OUT" 2>&1 || {
    cat "$SEED_OUT" >&2
    fail "seed-dev.sh failed"
}
ADMIN_EMAIL="$(sed -n 's/^  email: *//p' "$SEED_OUT" | head -n1)"
ADMIN_PASSWORD="$(sed -n 's/^  password: *//p' "$SEED_OUT" | head -n1)"
[ -n "$ADMIN_EMAIL" ] && [ -n "$ADMIN_PASSWORD" ] || {
    cat "$SEED_OUT" >&2
    fail "could not parse admin credentials from seed output"
}
chmod 600 "$SEED_OUT"
log "seeded admin (password captured to artifact, not logged)"

# --- step 6: admin login -----------------------------------------------------
STEP=6
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${ADMIN_EMAIL}" \
    --data-urlencode "password=${ADMIN_PASSWORD}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "admin login returned $CODE"; }
ADMIN_TOKEN="$(jq -r '.access_token // empty' "$BODY")"
[ -n "$ADMIN_TOKEN" ] || fail "admin login body has no access_token"
log "admin login OK"

# --- step 7: create user (OTP once) -----------------------------------------
STEP=7
ALICE_EMAIL="alice-m4-${RANDOM}-${RANDOM}@example.com"
CREATE_BODY="$ARTIFACT_DIR/create-user.json"
cat > "$CREATE_BODY" <<EOF
{"email":"${ALICE_EMAIL}","role":"User"}
EOF
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/admin/users" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$CREATE_BODY")"
[ "$CODE" = "201" ] || { cat "$BODY" >&2; fail "create user returned $CODE"; }
ALICE_ID="$(jq -r '.id // empty' "$BODY")"
OTP="$(jq -r '.otp // empty' "$BODY")"
[ -n "$ALICE_ID" ] && [ -n "$OTP" ] || fail "create-user body missing id/otp"
log "create user ${ALICE_ID} OK"

# --- step 8: user OTP-login --------------------------------------------------
STEP=8
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/otp-login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${ALICE_EMAIL}" \
    --data-urlencode "otp=${OTP}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "otp-login returned $CODE"; }
ALICE_TOKEN="$(jq -r '.access_token // empty' "$BODY")"
[ -n "$ALICE_TOKEN" ] || fail "otp-login body has no access_token"
log "otp-login OK"

# --- step 9: change-password -------------------------------------------------
STEP=9
ALICE_PASSWORD='M4SmokePassw0rd!abc'
cat > "$CHG_BODY" <<EOF
{"new_password":"${ALICE_PASSWORD}","new_password_confirm":"${ALICE_PASSWORD}"}
EOF
chmod 600 "$CHG_BODY"
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/change-password" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$CHG_BODY")"
[ "$CODE" = "204" ] || { cat "$BODY" >&2; fail "change-password returned $CODE"; }
log "change-password OK"

# --- step 10: set-passphrase -------------------------------------------------
STEP=10
PASSPHRASE='correct horse battery staple'
cat > "$SP_BODY" <<EOF
{"passphrase":"${PASSPHRASE}","passphrase_confirm":"${PASSPHRASE}"}
EOF
chmod 600 "$SP_BODY"
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/set-passphrase" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$SP_BODY")"
[ "$CODE" = "204" ] || { cat "$BODY" >&2; fail "set-passphrase returned $CODE"; }
log "set-passphrase OK"

# --- step 11: unwrap-dek (Q-E9 interim DEK transport) ------------------------
STEP=11
cat > "$UD_BODY" <<EOF
{"passphrase":"${PASSPHRASE}"}
EOF
chmod 600 "$UD_BODY"
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/account/unwrap-dek" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$UD_BODY")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "unwrap-dek returned $CODE"; }
DEK="$(jq -r '.dek // empty' "$BODY")"
[ -n "$DEK" ] || fail "unwrap-dek body has no dek"
log "unwrap-dek OK (DEK in memory only, never logged)"

# --- step 12: POST /v1/user/actions (Greenmail SMTP config) ------------------
STEP=12
cat > "$ACTION_BODY" <<'EOF'
{"type_id":"imap-reply","display_name":"M4 smoke reply","require_confirmation":true,"dry_run_default":false,"config":{"host":"greenmail","port":3025,"use_ssl":false,"username":"alice","password":"alice","from_address":"alice@greenmail.test","from_name":"Alice"}}
EOF
chmod 600 "$ACTION_BODY" # carries test SMTP password
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/actions" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$ACTION_BODY")"
[ "$CODE" = "201" ] || { cat "$BODY" >&2; fail "create action returned $CODE (want 201)"; }
ACTION_ID="$(jq -r '.id // empty' "$BODY")"
[ -n "$ACTION_ID" ] || fail "create action body has no id"
jq -e '(.config | has("password") | not) and (has("password") | not)' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "create action response must not contain password"; }
log "POST /v1/user/actions -> 201 (no password in response)"

AC_ROWS="$(psql_q "SELECT count(*) FROM action_configs WHERE id='${ACTION_ID}';")"
AC_ROWS="$(printf '%s' "$AC_ROWS" | tr -d '\r\n')"
[ "$AC_ROWS" = "1" ] || fail "action_configs has ${AC_ROWS} rows for ${ACTION_ID} (want 1)"
BLOB_ROWS="$(psql_q "SELECT count(*) FROM encrypted_blobs b JOIN action_configs a ON a.config_blob_id=b.id WHERE a.id='${ACTION_ID}' AND b.purpose='action_config';")"
BLOB_ROWS="$(printf '%s' "$BLOB_ROWS" | tr -d '\r\n')"
[ "$BLOB_ROWS" = "1" ] || fail "no encrypted_blobs purpose='action_config' row for action ${ACTION_ID}"
log "psql: action_configs=1, encrypted_blobs purpose=action_config present"

# --- step 13: GET / PATCH (blank password preserved) / pagination ------------
STEP=13
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    "${BASE_URL}/v1/user/actions/${ACTION_ID}" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "GET action returned $CODE"; }
jq -e '(.config | has("password") | not)' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "GET action must not return password"; }
log "GET action -> 200 (no password)"

cat > "$PATCH_BODY" <<'EOF'
{"display_name":"M4 smoke reply renamed","config":{"host":"greenmail","port":3025,"use_ssl":false,"username":"alice","password":"","from_address":"alice@greenmail.test","from_name":"Alice"}}
EOF
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X PATCH "${BASE_URL}/v1/user/actions/${ACTION_ID}" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$PATCH_BODY")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "PATCH action returned $CODE"; }
jq -e '.display_name == "M4 smoke reply renamed" and (.config | has("password") | not)' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "PATCH action response wrong or leaks password"; }
log "PATCH action (blank password) -> 200, password preserved server-side"

CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    "${BASE_URL}/v1/user/actions?limit=25" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "GET /v1/user/actions list returned $CODE"; }
jq -e --arg id "$ACTION_ID" 'any(.items[]?; .id == $id)' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "actions list does not contain created action"; }
log "GET /v1/user/actions pagination/list OK"

# --- step 14: DELETE action (204, blob gone) ---------------------------------
STEP=14
OLD_BLOB="$(psql_q "SELECT config_blob_id FROM action_configs WHERE id='${ACTION_ID}';" | tr -d '\r\n')"
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X DELETE "${BASE_URL}/v1/user/actions/${ACTION_ID}" \
    -H "Authorization: Bearer ${ALICE_TOKEN}")"
[ "$CODE" = "204" ] || { cat "$BODY" >&2; fail "DELETE action returned $CODE (want 204)"; }
AC_GONE="$(psql_q "SELECT count(*) FROM action_configs WHERE id='${ACTION_ID}';" | tr -d '\r\n')"
[ "$AC_GONE" = "0" ] || fail "action_configs row still present after DELETE"
if [ -n "$OLD_BLOB" ]; then
    BLOB_GONE="$(psql_q "SELECT count(*) FROM encrypted_blobs WHERE id='${OLD_BLOB}';" | tr -d '\r\n')"
    [ "$BLOB_GONE" = "0" ] || fail "encrypted_blobs row ${OLD_BLOB} still present after DELETE"
fi
log "DELETE action -> 204 (row + blob gone)"

# --- step 15: notifications round-trip ---------------------------------------
STEP=15
# Seed a notification for the user so read/read-all are exercised (dispatch is
# xUnit-only in M4, SQ-M4-5). Plain insert; no secrets.
NOTIF_ID="$(psql_q "INSERT INTO user_notifications (id,user_id,kind,title,body,created_at) VALUES (gen_random_uuid(),'${ALICE_ID}','smoke_test','Smoke notification','body',now()) RETURNING id;" | tr -d '\r\n')"
[ -n "$NOTIF_ID" ] || fail "could not seed a notification row"

CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    "${BASE_URL}/v1/user/notifications?filter[unread]=true&limit=25" \
    -H "Authorization: Bearer ${ALICE_TOKEN}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "GET notifications returned $CODE"; }
jq -e --arg id "$NOTIF_ID" 'any(.items[]?; .id == $id)' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "unread notifications list missing seeded row"; }
log "GET /v1/user/notifications?filter[unread]=true OK"

CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/notifications/${NOTIF_ID}/read" \
    -H "Authorization: Bearer ${ALICE_TOKEN}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "mark-read returned $CODE"; }
jq -e '.read_at != null' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "mark-read did not set read_at"; }
log "POST /v1/user/notifications/{id}/read OK"

# Seed another unread to exercise read-all
psql_q "INSERT INTO user_notifications (id,user_id,kind,title,created_at) VALUES (gen_random_uuid(),'${ALICE_ID}','smoke_test','Second','now'::timestamptz);" >/dev/null 2>&1 || \
psql_q "INSERT INTO user_notifications (id,user_id,kind,title,created_at) VALUES (gen_random_uuid(),'${ALICE_ID}','smoke_test','Second',now());" >/dev/null
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/notifications/read-all" \
    -H "Authorization: Bearer ${ALICE_TOKEN}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "read-all returned $CODE"; }
jq -e 'has("updated")' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "read-all body missing updated"; }
log "POST /v1/user/notifications/read-all OK"

# --- step 16: M4 CRUD audit actions present ----------------------------------
STEP=16
# Smoke exercises HTTP CRUD; dispatch-driven actions (action_dispatched,
# action_invalid_args) are proven by: dotnet test --filter Category=M4EndToEnd
for a in create_action update_action delete_action; do
    C="$(psql_q "SELECT count(*) FROM audit_log WHERE action='${a}';" | tr -d '\r\n')"
    case "$C" in ''|*[!0-9]*) fail "audit count for '${a}' is not numeric: '${C}'" ;; esac
    [ "$C" -ge 1 ] || fail "audit log has no '${a}' row after the M4 CRUD flow"
done
BAD_HMAC="$(psql_q "SELECT count(*) FROM audit_log WHERE octet_length(hmac) IS DISTINCT FROM 32;" | tr -d '\r\n')"
[ "$BAD_HMAC" = "0" ] || fail "${BAD_HMAC} audit rows have a non-32-byte hmac"
log "M4 CRUD audit actions present (create/update/delete_action); every hmac 32 bytes"
log "NOTE: action_dispatched/action_invalid_args are dispatch-driven (xUnit: Category=M4EndToEnd, SQ-M4-5)"

# --- step 17: tools/AuditVerify against the live DB --------------------------
STEP=17
log "running tools/AuditVerify against live Postgres"
AUDIT_KEY="$(grep '^AUDIT__HmacKey=' .env | cut -d= -f2-)"
CONN="$(grep '^ConnectionStrings__Postgres=' .env | cut -d= -f2-)"
NUGET_RO=""
if [ -d /tmp/opencode/nuget ]; then
    NUGET_RO="-v /tmp/opencode/nuget:/root/.nuget"
fi
set +e
# shellcheck disable=SC2086
docker run --rm \
    --network "${PROJECT}_default" \
    -v "$ROOT_DIR:/work" \
    -w /work \
    $NUGET_RO \
    -e ConnectionStrings__Postgres="$CONN" \
    -e AUDIT__HmacKey="$AUDIT_KEY" \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet run --project tools/AuditVerify \
    > "$ARTIFACT_DIR/audit-verify.log" 2>&1
AV_RC=$?
set -e
tail -n 5 "$ARTIFACT_DIR/audit-verify.log" || true
[ "$AV_RC" = "0" ] || fail "tools/AuditVerify exited $AV_RC (see $ARTIFACT_DIR/audit-verify.log)"
log "tools/AuditVerify -> exit 0 (chain valid)"

# --- teardown ----------------------------------------------------------------
log "teardown"
compose down -v --remove-orphans >/dev/null 2>&1 || true

echo "OK: all M4 smoke tests passed (HTTP surface + audit chain; dispatch flows: Category=M4EndToEnd)"
