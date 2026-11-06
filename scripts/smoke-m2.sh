#!/usr/bin/env bash
# M2T7 — scripted M2 sign-off smoke against the dev compose stack.
#
# The M2 exit-criteria command (milestone Verification strategy + task
# scripts/smoke-m2.sh contract): clean stack → migrations → seed admin →
# login flow → set-passphrase + unwrap-dek → add IMAP source (Greenmail) →
# test-connection {ok:true} → seed 3 messages (scripts/seed-mailbox.sh) →
# fetch-now {processed:3, new:3} → psql row assertions (count, ascending
# remote_uid, stage/engine columns) → second fetch-now new:0 (dedupe) →
# invalid credentials flow ({ok:false} 200 + bad-port RFC 7807 400) → the 5
# source audit actions → tampered drop-in (loader refuses with
# signature_mismatch + refusal-wins unregister + plugin-not-registered) →
# cleanup/reload → tools/AuditVerify exit 0.
#
# Exit 0 = every step passed ("OK: all M2 smoke tests passed"); a non-zero
# exit names the failing step number and dumps a sanitized diagnostics file
# (artifacts/m2t7/smoke-failure.log) before teardown.
#
# Adaptations vs the task file (recorded in the M2T7 task file):
#   * Compose is materialized as docker/docker-compose.dev.yml's `greenmail`
#     service (the plan-mode task wrote "design notes only").
#   * Seeding uses SMTP (curl smtp://) — greenmail/standalone:2.0.1 has NO
#     REST mail inject endpoint (verified against the pinned image), so the
#     task's REST-inject snippet cannot work; REST is used for readiness,
#     user provisioning and purge only.
#   * The tampered drop-in needs a COMPANION manifest claiming plugin_id
#     "imap" with a present-but-wrong signature: a bare tampered DLL alone
#     is refused as trust_anchor_mismatch (reason=unsigned), not the
#     signature_mismatch the acceptance criteria pin.
#
# Usage: bash scripts/smoke-m2.sh
# Env:   .env must exist (cp .env.example .env and fill throwaway values).
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

PROJECT="emailagent-m2-smoke"
ARTIFACT_DIR="artifacts/m2t7"
mkdir -p "$ARTIFACT_DIR"

log()  { printf '[m2t7] %s\n' "$*"; }
fail() { printf '[m2t7] FAIL (step %s): %s\n' "${STEP:-0}" "$*" >&2; exit 1; }

for tool in docker curl jq openssl; do
    command -v "$tool" >/dev/null 2>&1 || fail "required tool '$tool' not found"
done

[ -f .env ] || fail ".env missing — cp .env.example .env and fill throwaway values"

for k in POSTGRES_PASSWORD AUTH__SigningKey system_kek AUDIT__HmacKey PLUGIN__SigningKey ConnectionStrings__Postgres; do
    grep -Eq "^${k}=." .env || fail ".env has no value for $k"
done

# --- failure diagnostics (pattern: scripts/smoke.sh, M0T7) -------------------
# On any non-zero exit, dump a sanitized snapshot of THIS project to
# artifacts/m2t7/smoke-failure.log while the stack is still up (the inline
# teardown only runs on the success path). Whitelisted fields only — never
# full inspect JSON, never container env (secret material). Every command is
# guarded with `|| true` so diagnostics can never mask the real error.
FAILURE_LOG="$ARTIFACT_DIR/smoke-failure.log"
DIAG_DONE=0
rm -f "$FAILURE_LOG" 2>/dev/null || true

dump_diagnostics() {
    local status="$1"
    mkdir -p "$ARTIFACT_DIR" 2>/dev/null || true
    {
        echo "=== m2t7 smoke failure diagnostics ==="
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
    echo "M2 SMOKE FAILED (exit ${status}); diagnostics: ${FAILURE_LOG}" >&2 || true
}

on_exit() {
    local status=$?
    trap '' INT TERM HUP # a second signal mid-dump must not abort diagnostics
    set +e
    if [ "$status" -ne 0 ] && [ "$DIAG_DONE" != "1" ]; then
        DIAG_DONE=1
        dump_diagnostics "$status"
    fi
    rm -f "${BODY_FILE:-}" 2>/dev/null || true
    # Credential-bearing artifacts never survive the run (review S2) — the
    # Set-Cookie capture, the seeded admin's password output, the change-
    # password / set-passphrase / unwrap-dek request bodies. The diagnostics
    # dump above never reads these files (whitelisted fields only).
    rm -f "${HDRS:-}" "${SEED_OUT:-}" "${CHG_BODY:-}" "${SP_BODY:-}" "${UD_BODY:-}" \
        2>/dev/null || true
    exit "$status"
}
trap on_exit EXIT # THE exit trap: failure diagnostics + temp-file cleanup
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

compose() { docker compose -p "$PROJECT" "$@"; }

# Q-A13: every API call goes through the dev TLS edge (self-signed throwaway
# cert) — same posture as smoke-m1.sh; trust verification is skipped uniformly.
curl() { command curl --insecure "$@"; }

BODY_FILE="$(mktemp)"

HDRS="$ARTIFACT_DIR/smoke-headers.tmp"
: > "$HDRS" && chmod 600 "$HDRS" # may hold auth material — not world-readable
BODY="$BODY_FILE"

poll_code() {
    # url expected deadline_seconds
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

# psql single-row/single-value reader against the compose Postgres (stdin SQL).
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
# -v: volumes are namespaced to this project — a re-run starts from an empty
# database, so seed-dev.sh never trips over "admin already exists".
compose down -v --remove-orphans >/dev/null 2>&1 || true
compose up -d --build
log "compose up done"

# --- step 2: wait for /health/ready 200 through the edge ---------------------
STEP=2
PORT="$(compose port web 443 2>/dev/null | sed -n 's/.*://p')"
PORT="${PORT:-8443}"
BASE_URL="https://localhost:${PORT}"
log "probing ${BASE_URL}/health/ready"
poll_code "${BASE_URL}/health/ready" 200 300 || fail "/health/ready never returned 200 within 300s"
log "/health/ready -> 200"

# --- step 3: wait for Greenmail (REST readiness + host port discovery) -------
STEP=3
# Ports are discovered from the running project so .env overrides of
# GREENMAIL_SMTP_PORT / GREENMAIL_REST_PORT cannot desynchronise the script.
GMAIL_REST_PORT="$(compose port greenmail 8080 2>/dev/null | sed -n 's/.*://p' | head -n1)"
GMAIL_SMTP_PORT="$(compose port greenmail 3025 2>/dev/null | sed -n 's/.*://p' | head -n1)"
GMAIL_REST_PORT="${GMAIL_REST_PORT:-18080}"
GMAIL_SMTP_PORT="${GMAIL_SMTP_PORT:-3025}"
GREENMAIL_REST="http://localhost:${GMAIL_REST_PORT}"
GREENMAIL_SMTP="localhost:${GMAIL_SMTP_PORT}"
log "greenmail REST at ${GREENMAIL_REST}, SMTP at ${GREENMAIL_SMTP}"
poll_code "${GREENMAIL_REST}/api/service/readiness" 200 120 \
    || fail "Greenmail /api/service/readiness never returned 200 within 120s"
log "greenmail readiness -> 200 (strict auth: no -Dgreenmail.auth.disabled)"

# --- step 4: apply migrations (API does not auto-migrate) --------------------
STEP=4
# The api container runs the SDK-less aspnet:8.0 runtime, so `dotnet ef`
# runs in a throwaway SDK container on the compose network (smoke-m1.sh
# pattern); the design-time factory reads ConnectionStrings__Postgres.
log "applying EF migrations (SDK container on the compose network)"
PG_PASSWORD="$(grep '^POSTGRES_PASSWORD=' .env | cut -d= -f2-)"
NUGET_VOL=""
if [ -d /tmp/opencode/nuget ]; then
    NUGET_VOL="-v /tmp/opencode/nuget:/root/.nuget"
fi
# shellcheck disable=SC2086 # NUGET_VOL is intentionally word-split
docker run --rm \
    --network "${PROJECT}_default" \
    -v "$ROOT_DIR:/work" \
    -w /work \
    $NUGET_VOL \
    -e "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=emailagent;Username=emailagent;Password=${PG_PASSWORD}" \
    mcr.microsoft.com/dotnet/sdk:8.0 \
    sh -c '
        dotnet tool install --global dotnet-ef --version 8.0.* >/dev/null 2>&1 || true
        export PATH="$PATH:/root/.dotnet/tools"
        dotnet ef database update --project src/EmailAgent.Infrastructure
    ' > "$ARTIFACT_DIR/migrate.log" 2>&1 || {
    cat "$ARTIFACT_DIR/migrate.log" >&2
    fail "dotnet ef database update failed (see $ARTIFACT_DIR/migrate.log)"
}
log "migrations applied"

# The api opened its Npgsql pool before the schema existed — restart api/web
# so the process pools attach to the migrated schema (smoke-m1.sh rationale).
log "restarting api/web so Npgsql sees the migrated schema"
compose restart api web >/dev/null 2>&1
poll_code "${BASE_URL}/health/ready" 200 120 \
    || fail "/health/ready never returned 200 after api restart"

# --- step 5: seed the first admin -------------------------------------------
STEP=5
log "running scripts/seed-dev.sh (COMPOSE_PROJECT_NAME=$PROJECT)"
SEED_OUT="$ARTIFACT_DIR/seed.out"
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
log "seeded admin (password captured to artifact, not logged)"
chmod 600 "$SEED_OUT"

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

# --- step 7: create the regular user (OTP once) -----------------------------
STEP=7
ALICE_EMAIL="alice-m2-${RANDOM}-${RANDOM}@example.com"
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

# --- step 9: change-password (first-login path) -----------------------------
STEP=9
ALICE_PASSWORD='M2SmokePassw0rd!abc'
CHG_BODY="$ARTIFACT_DIR/change-password.json"
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
SP_BODY="$ARTIFACT_DIR/set-passphrase.json"
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

# --- step 11: unwrap-dek (amendment R2 — the only M2 DEK source) -------------
STEP=11
UD_BODY="$ARTIFACT_DIR/unwrap-dek.json"
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

# --- step 12: add the IMAP source (Greenmail, alice/alice) -------------------
STEP=12
# Config matches the compose topology: service name `greenmail`, internal
# IMAP port 3143, plaintext (3143 advertises no STARTTLS).
SRC_BODY="$ARTIFACT_DIR/create-source.json"
cat > "$SRC_BODY" <<EOF
{"type_id":"imap","display_name":"greenmail inbox","config":{"host":"greenmail","port":3143,"use_ssl":false,"username":"alice","password":"alice","mailbox":"INBOX"}}
EOF
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/sources" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$SRC_BODY")"
[ "$CODE" = "201" ] || { cat "$BODY" >&2; fail "create source returned $CODE"; }
SOURCE_ID="$(jq -r '.id // empty' "$BODY")"
[ -n "$SOURCE_ID" ] || fail "create source body has no id"
jq -e 'has("password") | not' "$BODY" >/dev/null \
    || fail "create-source response must never contain a password member"
log "source ${SOURCE_ID} created (no password in the response)"

# Greenmail ships with NO default users (GreenMailFixture.cs, M2T4, does the
# same POST /api/user at fixture init) and this stack runs STRICT auth — so
# step 13's {ok:true} IMAP login needs alice provisioned first. seed-mailbox.sh
# would do it, but it only runs at step 14; provision here with the same
# recipe and the same acceptance (500 = "already exists" in this image).
GMAIL_USER_CODE="$(curl -s -o /dev/null -w '%{http_code}' -X POST "${GREENMAIL_REST}/api/user" \
    -H 'Content-Type: application/json' \
    --data '{"login":"alice","password":"alice","email":"alice@greenmail.test"}')"
case "$GMAIL_USER_CODE" in
    200 | 201 | 500)
        log "greenmail test user present (POST /api/user answered $GMAIL_USER_CODE)"
        ;;
    *)
        fail "greenmail POST /api/user answered $GMAIL_USER_CODE"
        ;;
esac

# --- step 13: test-connection -> {ok:true} -----------------------------------
STEP=13
TC_BODY="$ARTIFACT_DIR/test-connection.json"
cat > "$TC_BODY" <<EOF
{"type_id":"imap","config":{"host":"greenmail","port":3143,"use_ssl":false,"username":"alice","password":"alice","mailbox":"INBOX"}}
EOF
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/sources/test-connection" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$TC_BODY")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "test-connection returned $CODE (want 200)"; }
jq -e '.ok == true' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "test-connection ok is not true"; }
log "test-connection -> 200 {ok:true}"

# --- step 14: seed the mailbox (subjects A, B, C) ----------------------------
STEP=14
SEED_OUT2="$ARTIFACT_DIR/seed-mailbox.out"
GREENMAIL_REST="$GREENMAIL_REST" GREENMAIL_SMTP="$GREENMAIL_SMTP" \
    bash scripts/seed-mailbox.sh >"$SEED_OUT2" 2>&1 || {
    cat "$SEED_OUT2" >&2
    fail "seed-mailbox.sh failed"
}
tail -n 5 "$SEED_OUT2"
log "mailbox seeded (3 messages)"

# --- step 15: fetch-now -> processed 3 / new 3 -------------------------------
STEP=15
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/sources/${SOURCE_ID}/fetch-now" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "fetch-now returned $CODE"; }
jq -e '.processed == 3 and .new_processed_messages == 3' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "fetch-now must report processed=3, new_processed_messages=3"; }
LAST_UID="$(jq -r '.checkpoint.last_uid // empty' "$BODY")"
[ -n "$LAST_UID" ] || fail "fetch-now checkpoint has no last_uid"
log "fetch-now -> processed=3 new=3 (checkpoint last_uid=${LAST_UID})"

# --- step 16: psql row assertions --------------------------------------------
STEP=16
COUNT="$(psql_q "SELECT count(*) FROM processed_messages WHERE source_id = '${SOURCE_ID}';")"
COUNT="$(printf '%s' "$COUNT" | tr -d '\r\n')"
[ "$COUNT" = "3" ] || fail "processed_messages count is '${COUNT}' (want 3)"
log "count(*) -> 3"

# remote_uid in insertion order must ascend (rows land as the connector
# yields them; created_at is the write order, remote_uid breaks same-tick
# ties so the assertion is deterministic).
UIDS="$(psql_q "SELECT string_agg(remote_uid, ',' ORDER BY created_at, remote_uid::bigint) FROM processed_messages WHERE source_id = '${SOURCE_ID}';")"
UIDS="$(printf '%s' "$UIDS" | tr -d '\r\n')"
UID_COUNT="$(printf '%s\n' "$UIDS" | tr ',' '\n' | sed '/^$/d' | wc -l | tr -d ' ')"
[ "$UID_COUNT" = "3" ] || fail "expected 3 remote_uids, got '${UIDS}'"
PREV=0
for uid in $(printf '%s' "$UIDS" | tr ',' ' '); do
    case "$uid" in
        '' | *[!0-9]*) fail "remote_uid '${uid}' is not numeric (got: ${UIDS})" ;;
    esac
    [ "$uid" -ge "$PREV" ] || fail "remote_uids not ascending: ${UIDS}"
    PREV="$uid"
done
log "remote_uid ascending: ${UIDS}"

# Every row sits at the §6 initial values — no LLM/engine column touched.
VIOLATIONS="$(psql_q "SELECT count(*) FROM processed_messages WHERE source_id = '${SOURCE_ID}' AND NOT (stage = 'fetched' AND prefilter_matched = false AND model_decision_json IS NULL AND prompt_tokens IS NULL);")"
VIOLATIONS="$(printf '%s' "$VIOLATIONS" | tr -d '\r\n')"
[ "$VIOLATIONS" = "0" ] \
    || fail "${VIOLATIONS} rows violate stage='fetched'/prefilter_matched=false/NULL engine columns"
log "stage='fetched', prefilter_matched=false, model_decision_json/prompt_tokens NULL on all rows"

# --- step 17: second fetch-now -> dedupe (new = 0, count still 3) ------------
STEP=17
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/sources/${SOURCE_ID}/fetch-now" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "second fetch-now returned $CODE"; }
jq -e '.new_processed_messages == 0' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "second fetch-now must report new_processed_messages=0"; }
COUNT="$(psql_q "SELECT count(*) FROM processed_messages WHERE source_id = '${SOURCE_ID}';")"
COUNT="$(printf '%s' "$COUNT" | tr -d '\r\n')"
[ "$COUNT" = "3" ] || fail "after second fetch count is '${COUNT}' (want 3, not 6)"
log "second fetch-now -> new=0, count still 3 (UNIQUE(source_id, remote_uid))"

# --- step 18: flow 2a — wrong password -> 200 {ok:false} ---------------------
STEP=18
BAD_TC_BODY="$ARTIFACT_DIR/test-connection-bad.json"
cat > "$BAD_TC_BODY" <<EOF
{"type_id":"imap","config":{"host":"greenmail","port":3143,"use_ssl":false,"username":"alice","password":"definitely-not-the-password","mailbox":"INBOX"}}
EOF
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/sources/test-connection" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$BAD_TC_BODY")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "bad-password test-connection returned $CODE (want 200: failure lives in the body)"; }
jq -e '(.ok == false) and ((.error | type) == "string") and (.error | startswith("authentication failed"))' \
    "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "bad-password test-connection must be {ok:false, error:\"authentication failed…\"}"; }
log "flow 2a: wrong password -> 200 {ok:false, error~\"authentication failed\"}"

# --- step 19: flow 2b — port=99999 -> RFC 7807 400 ---------------------------
STEP=19
BAD_PORT_BODY="$ARTIFACT_DIR/create-source-bad-port.json"
cat > "$BAD_PORT_BODY" <<EOF
{"type_id":"imap","display_name":"bad port","config":{"host":"greenmail","port":99999,"use_ssl":false,"username":"alice","password":"alice","mailbox":"INBOX"}}
EOF
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/sources" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$BAD_PORT_BODY")"
[ "$CODE" = "400" ] || { cat "$BODY" >&2; fail "bad-port create returned $CODE (want 400)"; }
grep -qi '^content-type:.*application/problem+json' "$HDRS" \
    || fail "bad-port 400 is not application/problem+json"
jq -e --arg reason 'must be between 1 and 65535' \
    '(.type == "/errors/config-validation")
     and any(.errors[]; .name == "port" and .reason == $reason)' \
    "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "bad-port 400 must carry errors[{name:port, reason:must be between 1 and 65535}]; note jsonb reformatting is not in play here (wire JSON)"; }
log "flow 2b: port=99999 -> 400 /errors/config-validation (port reason pinned)"

# --- step 20: audit rows for the 5 source actions ----------------------------
STEP=20
# create_source + test_source_connection + fetch_source_now already ran on the
# main source; exercise update_source and delete_source on a throwaway second
# source so all five §15 actions exist for this actor.
SECOND_BODY="$ARTIFACT_DIR/create-source-2.json"
cat > "$SECOND_BODY" <<EOF
{"type_id":"imap","display_name":"throwaway","config":{"host":"greenmail","port":3143,"use_ssl":false,"username":"alice","password":"alice","mailbox":"INBOX"}}
EOF
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/sources" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$SECOND_BODY")"
[ "$CODE" = "201" ] || { cat "$BODY" >&2; fail "second create source returned $CODE"; }
SECOND_ID="$(jq -r '.id // empty' "$BODY")"
[ -n "$SECOND_ID" ] || fail "second create source body has no id"

CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X PATCH "${BASE_URL}/v1/user/sources/${SECOND_ID}" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H "X-User-Dek: ${DEK}" \
    -H 'Content-Type: application/json' \
    --data '{"display_name":"throwaway renamed"}')"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "PATCH source returned $CODE (want 200)"; }

CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X DELETE "${BASE_URL}/v1/user/sources/${SECOND_ID}" \
    -H "Authorization: Bearer ${ALICE_TOKEN}")"
[ "$CODE" = "204" ] || { cat "$BODY" >&2; fail "DELETE source returned $CODE (want 204)"; }

AUDIT_ACTIONS="$(psql_q "SELECT string_agg(action, ',') FROM audit_log WHERE actor_user_id = '${ALICE_ID}';")"
AUDIT_ACTIONS="$(printf '%s' "$AUDIT_ACTIONS" | tr -d '\r\n')"
for a in create_source update_source delete_source test_source_connection fetch_source_now; do
    printf '%s' ",${AUDIT_ACTIONS}," | grep -q ",${a}," \
        || fail "audit log missing action '${a}' (have: ${AUDIT_ACTIONS})"
done
BAD_HMAC="$(psql_q "SELECT count(*) FROM audit_log WHERE octet_length(hmac) IS DISTINCT FROM 32;")"
BAD_HMAC="$(printf '%s' "$BAD_HMAC" | tr -d '\r\n')"
[ "$BAD_HMAC" = "0" ] || fail "${BAD_HMAC} audit rows have a non-32-byte hmac"
log "all 5 source audit actions present; every hmac is 32 bytes"

# --- step 21: flow 3 — tampered drop-in is refused (refusal-wins) ------------
STEP=21
# The tampered artifact needs BOTH files under /plugins (the loader's default
# drop folder): a flipped-byte EmailAgent.Connectors.Imap.dll AND a companion
# EmailAgent.Connectors.Imap.plugin.json claiming plugin_id "imap" whose
# signature is present but wrong. The companion is what routes the refusal
# through gate 2 as reason=signature_mismatch (a bare tampered DLL is refused
# earlier as trust_anchor_mismatch/unsigned by file name), and its plugin_id
# claim is what keys the refusal-wins unregister.
TAMPER_DLL="$ARTIFACT_DIR/EmailAgent.Connectors.Imap.dll"
TAMPER_MANIFEST="$ARTIFACT_DIR/EmailAgent.Connectors.Imap.plugin.json"
log "extracting the shipped connector from the api container"
compose cp api:/app/EmailAgent.Connectors.Imap.dll "$TAMPER_DLL" \
    || fail "could not copy the connector out of the api container"
DLL_SIZE="$(wc -c < "$TAMPER_DLL" | tr -d ' ')"
[ "$DLL_SIZE" -gt 1 ] || fail "copied connector implausibly small (${DLL_SIZE} bytes)"
printf '\377' | dd of="$TAMPER_DLL" bs=1 seek=$((DLL_SIZE - 1)) conv=notrunc 2>/dev/null \
    || fail "could not flip a byte of the extracted connector"
cat > "$TAMPER_MANIFEST" <<'EOF'
{
  "plugin_id": "imap",
  "version": "9.9.9",
  "assembly": "EmailAgent.Connectors.Imap.dll",
  "type": "EmailAgent.Connectors.Imap.ImapMessageSource",
  "capabilities": ["processed_messages_write"],
  "http_allowlist": [],
  "notes": "M2T7 smoke flow 3 — tampered drop-in claiming the DEFAULT imap id (signature present but wrong).",
  "signature": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
}
EOF
# The runtime user (emailagent, uid 10001) must be able to read the drop
# folder; /plugins does not exist in the image (the default is only scanned
# when present), so create + hand it to the runtime user as root first.
compose exec -T -u root api sh -c 'mkdir -p /plugins && chown emailagent:emailagent /plugins' \
    || fail "could not create /plugins in the api container"
compose cp "$TAMPER_DLL" api:/plugins/EmailAgent.Connectors.Imap.dll \
    || fail "could not plant the tampered connector"
compose cp "$TAMPER_MANIFEST" api:/plugins/EmailAgent.Connectors.Imap.plugin.json \
    || fail "could not plant the tampered companion manifest"
compose exec -T -u root api chown emailagent:emailagent \
    /plugins/EmailAgent.Connectors.Imap.dll \
    /plugins/EmailAgent.Connectors.Imap.plugin.json \
    || fail "could not hand the tampered drop-in to the runtime user"

log "restarting api to force a fresh loader cycle"
compose restart api >/dev/null 2>&1
poll_code "${BASE_URL}/health/ready" 200 120 \
    || fail "/health/ready never returned 200 after tamper restart"

# (a) the log names the signature refusal. Capture the log first: piping
# `compose logs` straight into `grep -q` under `set -o pipefail` can fail on
# SIGPIPE (grep -q exits at the match while docker is still writing, and api
# logs exceed the pipe buffer across restarts) — a false negative on a gate
# that must stay deterministic.
API_LOGS="$(compose logs --no-color api 2>&1 || true)"
printf '%s\n' "$API_LOGS" | grep -q 'signature_mismatch' \
    || fail "api log has no signature_mismatch line for the tampered drop-in"
# (b) the audit row exists with reason + the claimed plugin_id.
REJECTS="$(psql_q "SELECT count(*) FROM audit_log WHERE action = 'plugin_rejected' AND metadata_json ->> 'reason' = 'signature_mismatch' AND metadata_json ->> 'plugin_id' = 'imap';")"
REJECTS="$(printf '%s' "$REJECTS" | tr -d '\r\n')"
[ "$REJECTS" -ge 1 ] || fail "no plugin_rejected/signature_mismatch row for plugin_id=imap"
# (c) refusal-wins: the DEFAULT imap no longer resolves, so test-connection
#     with type_id=imap fails at the registry gate BEFORE any socket opens.
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/sources/test-connection" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$TC_BODY")"
[ "$CODE" = "400" ] || { cat "$BODY" >&2; fail "tampered test-connection returned $CODE (want 400)"; }
jq -e '.type == "/errors/plugin-not-registered"' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "tampered test-connection type is not /errors/plugin-not-registered"; }
log "flow 3: tampered drop-in refused (signature_mismatch + plugin-not-registered)"

# --- step 22: cleanup — remove the tampered files and reload -----------------
STEP=22
compose exec -T api rm -f \
    /plugins/EmailAgent.Connectors.Imap.dll \
    /plugins/EmailAgent.Connectors.Imap.plugin.json \
    || fail "could not remove the tampered drop-in"
log "tampered drop-in removed; restarting api"
compose restart api >/dev/null 2>&1
poll_code "${BASE_URL}/health/ready" 200 120 \
    || fail "/health/ready never returned 200 after cleanup restart"
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/user/sources/test-connection" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$TC_BODY")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "post-cleanup test-connection returned $CODE"; }
jq -e '.ok == true' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "post-cleanup test-connection ok is not true (DEFAULT imap must be back)"; }
log "cleanup: DEFAULT imap restored (test-connection -> 200 {ok:true})"

# --- step 23: tools/AuditVerify against the live DB --------------------------
STEP=23
log "running tools/AuditVerify against live Postgres"
AUDIT_KEY="$(grep '^AUDIT__HmacKey=' .env | cut -d= -f2-)"
CONN="$(grep '^ConnectionStrings__Postgres=' .env | cut -d= -f2-)"
NUGET_RO=""
if [ -d /tmp/opencode/nuget ]; then
    NUGET_RO="-v /tmp/opencode/nuget:/root/.nuget"
fi
set +e
# shellcheck disable=SC2086 # NUGET_RO is intentionally word-split
docker run --rm \
    --network "${PROJECT}_default" \
    -v "$ROOT_DIR:/work" \
    -w /work \
    $NUGET_RO \
    -e ConnectionStrings__Postgres="$CONN" \
    -e AUDIT__HmacKey="$AUDIT_KEY" \
    mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet run --project tools/AuditVerify \
    > "$ARTIFACT_DIR/audit-verify.log" 2>&1
AV_RC=$?
set -e
tail -n 5 "$ARTIFACT_DIR/audit-verify.log" || true
[ "$AV_RC" = "0" ] || fail "tools/AuditVerify exited $AV_RC (see $ARTIFACT_DIR/audit-verify.log)"
log "tools/AuditVerify -> exit 0 (chain valid through every M2 flow)"

# --- teardown ----------------------------------------------------------------
log "teardown"
# -v: leave no database behind so the next run starts clean (m1 contract).
compose down -v --remove-orphans >/dev/null 2>&1 || true

echo "OK: all M2 smoke tests passed"
