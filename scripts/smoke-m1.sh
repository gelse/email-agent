#!/usr/bin/env bash
# M1T9 — scripted first-login smoke against the dev compose stack.
#
# The M1 sign-off command: clean stack → migrations → seed → full API flow
# (admin login → create Alice → OTP once → logout → Alice OTP-login →
# change-password → set-passphrase → DEK column checks → logout → re-login)
# → audit-row assertions → tools/AuditVerify exit 0 → refresh-reuse flow →
# rate-limit flow (6th login = 429 + Retry-After).
#
# Exit 0 = every step passed ("OK: all M1 smoke tests passed"); a non-zero
# exit names the failing step number.
#
# Q-A13 (resolved): the dev edge serves HTTPS on :443 with a throwaway
# self-signed certificate (scripts/gen-dev-cert.sh) and proxies to the api
# as plain HTTP — the same posture as traefik's external TLS termination in
# testing/prod. The whole run therefore goes through
# https://localhost:${WEB_HTTPS_PORT} with curl -k (self-signed trust is not
# configured), which makes the `secure` cookie-attribute assertions below
# meaningful over a real TLS path: a browser would now actually store the
# Secure ea_refresh cookie. The script parses `ea_refresh` out of Set-Cookie
# itself (never a cookie jar) and asserts the contract
# (name / Path / HttpOnly / Secure / SameSite=Lax) — asserted, never changed.
#
# Usage: bash scripts/smoke-m1.sh
# Env:   .env must exist (cp .env.example .env && fill throwaway values).
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

PROJECT="emailagent-m1-smoke"
ARTIFACT_DIR="artifacts/m1t9"
mkdir -p "$ARTIFACT_DIR"

log()  { printf '[m1t9] %s\n' "$*"; }
fail() { printf '[m1t9] FAIL (step %s): %s\n' "${STEP:-0}" "$*" >&2; exit 1; }

for tool in docker curl jq openssl; do
    command -v "$tool" >/dev/null 2>&1 || fail "required tool '$tool' not found"
done

[ -f .env ] || fail ".env missing — cp .env.example .env and fill throwaway values"

for k in POSTGRES_PASSWORD AUTH__SigningKey system_kek AUDIT__HmacKey PLUGIN__SigningKey ConnectionStrings__Postgres; do
    grep -Eq "^${k}=." .env || fail ".env has no value for $k"
done

# Q-A13: every call below talks to the dev TLS edge (self-signed throwaway
# cert), so trust verification is skipped uniformly — a shell function keeps
# the individual curl calls readable instead of repeating -k 20 times.
curl() { command curl --insecure "$@"; }

COOKIE_JAR="$(mktemp)"
BODY_FILE="$(mktemp)"
cleanup() {
    rm -f "$COOKIE_JAR" "$BODY_FILE" 2>/dev/null || true
}
trap cleanup EXIT

compose() { docker compose -p "$PROJECT" "$@"; }

# --- helpers ---------------------------------------------------------------

# Extract the raw ea_refresh value from a Set-Cookie response header file.
# $1 = headers file. Echoes the value; fails (exit 1) if absent.
refresh_cookie_from_headers() {
    local raw
    raw="$(grep -i '^set-cookie:' "$1" | grep -i 'ea_refresh=' | head -n1 || true)"
    [ -n "$raw" ] || return 1
    # Strip "Set-Cookie: ea_refresh=" prefix and everything from the first ';'.
    raw="${raw#*: }"
    raw="${raw#ea_refresh=}"
    printf '%s' "${raw%%;*}"
}

# POST with an explicit Cookie header: the value was parsed from Set-Cookie
# above (kept from the plain-HTTP era; harmless over HTTPS and lets the
# script assert rotation byte-for-byte instead of trusting a jar).
post_with_cookie() {
    # $1=url  $2=cookie value  $3=body file (optional)
    local url="$1" cookie="$2" body="${3:-}"
    if [ -n "$body" ]; then
        curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
            -X POST "$url" \
            -H "Cookie: ea_refresh=${cookie}" \
            -H 'Content-Type: application/json' \
            --data-binary "@$body"
    else
        curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
            -X POST "$url" \
            -H "Cookie: ea_refresh=${cookie}"
    fi
}

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

HDRS="$ARTIFACT_DIR/smoke-headers.tmp"
# Credential hygiene: the header file carries live ea_refresh Set-Cookie
# values, so pre-create it mode 600 (curl -D truncates in place, keeping the
# inode and mode for the rest of the run).
: > "$HDRS" && chmod 600 "$HDRS"
BODY="$BODY_FILE"

# --- step 1: compose up ----------------------------------------------------
STEP=1
log "ensuring dev TLS certificate (scripts/gen-dev-cert.sh)"
bash scripts/gen-dev-cert.sh || fail "gen-dev-cert.sh failed"
log "compose up (project=$PROJECT)"
# -v: volumes are namespaced to this project, so a re-run must start from an
# empty database — otherwise step 4's seed-dev.sh refuses ("already exists").
compose down -v --remove-orphans >/dev/null 2>&1 || true
compose up -d --build
log "compose up done"

# --- step 2: wait for /health/ready 200 ------------------------------------
STEP=2
# Q-A13: probe through the HTTPS edge (nginx :443 → api), curl -k for the
# self-signed dev certificate; falls back to the compose-published port.
PORT="$(compose port web 443 2>/dev/null | sed -n 's/.*://p')"
PORT="${PORT:-8443}"
BASE_URL="https://localhost:${PORT}"
log "probing ${BASE_URL}/health/ready"
poll_code "${BASE_URL}/health/ready" 200 300 || fail "/health/ready never returned 200 within 300s"
log "/health/ready -> 200"

# --- step 3: apply migrations (API does not auto-migrate) -------------------
STEP=3
# The api container runs the SDK-less aspnet:10.0 runtime, so `dotnet ef`
# cannot live there ("SDK not found"); run it in a throwaway SDK container
# attached to the compose network instead (binds use host paths, which the
# daemon resolves). The design-time factory reads ConnectionStrings__Postgres
# and Infrastructure implements IDesignTimeDbContextFactory, so no
# --startup-project is needed.
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

# The api process opened its Npgsql pool BEFORE the schema existed (the
# /health/ready poll and the audit-chain startup check both hit the DB ahead
# of migrations), so its Npgsql type catalog predates the migration's
# `CREATE EXTENSION citext` — every later read of users.email fails with
# "Reading as 'System.String' is not supported for fields having DataTypeName
# '-'". Restarting api (and web, so nginx re-resolves the upstream) gives the
# process a fresh pool against the migrated schema.
log "restarting api/web so Npgsql sees the migrated schema"
compose restart api web >/dev/null 2>&1
poll_code "${BASE_URL}/health/ready" 200 120 \
    || fail "/health/ready never returned 200 after api restart"

# --- step 4: seed the first admin ------------------------------------------
STEP=4
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
log "seeded admin ${ADMIN_EMAIL} (password captured to artifact, not logged)"
chmod 600 "$SEED_OUT"

# --- step 5: admin login ----------------------------------------------------
STEP=5
LOGIN_BODY="$ARTIFACT_DIR/login.json"
cat > "$LOGIN_BODY" <<EOF
{"email":"${ADMIN_EMAIL}","password":"${ADMIN_PASSWORD}"}
EOF
chmod 600 "$LOGIN_BODY" # contains the bootstrap password — not world-readable
# Login is form-encoded per §13.3.
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${ADMIN_EMAIL}" \
    --data-urlencode "password=${ADMIN_PASSWORD}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "admin login returned $CODE"; }
ADMIN_TOKEN="$(jq -r '.access_token // empty' "$BODY")"
[ -n "$ADMIN_TOKEN" ] || fail "admin login body has no access_token"
COOKIE1="$(refresh_cookie_from_headers "$HDRS")" || fail "admin login set no ea_refresh cookie"
# Assert the pinned cookie attributes (Q-A7 / SQ-4) without changing them.
grep -qi 'ea_refresh=' "$HDRS" || fail "no ea_refresh in Set-Cookie"
grep -qi 'path=/v1/account/refresh' "$HDRS" || fail "ea_refresh Path is not /v1/account/refresh"
grep -qi 'httponly' "$HDRS" || fail "ea_refresh is not HttpOnly"
grep -qi 'secure' "$HDRS" || fail "ea_refresh is not Secure (Q-A7 contract)"
grep -qi 'samesite=lax' "$HDRS" || fail "ea_refresh is not SameSite=Lax"
log "admin login OK (cookie attributes verified)"

# --- step 6: create Alice (OTP once) ---------------------------------------
STEP=6
ALICE_EMAIL="alice-${RANDOM}-${RANDOM}@example.com"
CREATE_BODY="$ARTIFACT_DIR/create-user.json"
cat > "$CREATE_BODY" <<EOF
{"email":"${ALICE_EMAIL}","role":"User"}
EOF
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/admin/users" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$CREATE_BODY")"
[ "$CODE" = "201" ] || { cat "$BODY" >&2; fail "create user returned $CODE"; }
ALICE_ID="$(jq -r '.id // empty' "$BODY")"
OTP="$(jq -r '.otp // empty' "$BODY")"
[ -n "$ALICE_ID" ] && [ -n "$OTP" ] || fail "create-user body missing id/otp"
# Exactly one OTP occurrence in the raw body (Q-A3 view-once).
OCCURRENCES="$(grep -o "$OTP" "$BODY" | wc -l | tr -d ' ')"
[ "$OCCURRENCES" = "1" ] || fail "OTP appears $OCCURRENCES times in create response (want 1)"
# §13.14: every AUTHENTICATED response carries the X-RateLimit-* budget
# headers (this admin create-user call is one of them).
grep -qi '^x-ratelimit-limit:' "$HDRS" \
    || fail "authenticated create-user response missing X-RateLimit-Limit"
grep -qi '^x-ratelimit-remaining:' "$HDRS" \
    || fail "authenticated create-user response missing X-RateLimit-Remaining"
log "create user ${ALICE_ID} OK (OTP appears exactly once; X-RateLimit-* present)"

# --- step 7: admin logout ---------------------------------------------------
STEP=7
CODE="$(post_with_cookie "${BASE_URL}/v1/account/logout" "$COOKIE1")"
[ "$CODE" = "204" ] || { cat "$BODY" >&2; fail "admin logout returned $CODE"; }
grep -qi 'max-age=0' "$HDRS" || grep -qi '1970' "$HDRS" \
    || fail "logout did not expire the ea_refresh cookie"
log "admin logout OK"

# --- step 8: Alice OTP-login ------------------------------------------------
STEP=8
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/otp-login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${ALICE_EMAIL}" \
    --data-urlencode "otp=${OTP}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "otp-login returned $CODE"; }
jq -e '.must_complete_first_login == true' "$BODY" >/dev/null \
    || fail "otp-login must_complete_first_login is not true"
grep -qi 'x-login-state: must-complete-first-login' "$HDRS" \
    || fail "otp-login missing X-Login-State: must-complete-first-login"
ALICE_TOKEN="$(jq -r '.access_token // empty' "$BODY")"
ALICE_COOKIE="$(refresh_cookie_from_headers "$HDRS")" || fail "otp-login set no ea_refresh cookie"
[ -n "$ALICE_TOKEN" ] || fail "otp-login body has no access_token"
# Reuse of the same OTP must be refused (single-use).
CODE="$(curl -sS -o /dev/null -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/otp-login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${ALICE_EMAIL}" \
    --data-urlencode "otp=${OTP}")"
[ "$CODE" = "401" ] || fail "reused OTP returned $CODE (want 401)"
log "Alice otp-login OK (must_complete_first_login=true, single-use OTP verified)"

# --- step 9: change-password (first-login path) ----------------------------
STEP=9
CHG_BODY="$ARTIFACT_DIR/change-password.json"
ALICE_PASSWORD='SmokePassw0rd!abc'
cat > "$CHG_BODY" <<EOF
{"new_password":"${ALICE_PASSWORD}","new_password_confirm":"${ALICE_PASSWORD}"}
EOF
chmod 600 "$CHG_BODY" # live password — not world-readable
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/change-password" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$CHG_BODY")"
[ "$CODE" = "204" ] || { cat "$BODY" >&2; fail "change-password returned $CODE"; }
log "change-password OK (first-login shape, no old_password)"

# --- step 10: set-passphrase ------------------------------------------------
STEP=10
PASSPHRASE='correct horse battery staple'
SP_BODY="$ARTIFACT_DIR/set-passphrase.json"
cat > "$SP_BODY" <<EOF
{"passphrase":"${PASSPHRASE}","passphrase_confirm":"${PASSPHRASE}"}
EOF
chmod 600 "$SP_BODY" # live passphrase — not world-readable
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/set-passphrase" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$SP_BODY")"
[ "$CODE" = "204" ] || { cat "$BODY" >&2; fail "set-passphrase returned $CODE"; }

# --- step 11: DB assertions (dek columns + audit rows) ----------------------
STEP=11
KEY_MATERIAL="$(compose exec -T postgres sh -c '
    PGPASSWORD="$(cat /run/secrets/postgres_password)"
    export PGPASSWORD
    exec psql -h 127.0.0.1 -U emailagent -d emailagent -tA -v ON_ERROR_STOP=1
' <<SQL
SELECT concat_ws('|',
    (dek_wrapped IS NOT NULL)::int,
    (dek_nonce   IS NOT NULL)::int,
    (kdf_salt    IS NOT NULL)::int,
    (kdf_params  IS NOT NULL)::int)
FROM users WHERE id = '${ALICE_ID}';
SQL
)"
KEY_MATERIAL="$(printf '%s' "$KEY_MATERIAL" | tr -d '\r\n')"
[ "$KEY_MATERIAL" = "1|1|1|1" ] \
    || fail "key-material columns not all populated (got '${KEY_MATERIAL}')"
log "dek_wrapped/dek_nonce/kdf_salt/kdf_params all populated"

# --- step 12: Alice logout + re-login ---------------------------------------
STEP=12
CODE="$(post_with_cookie "${BASE_URL}/v1/account/logout" "$ALICE_COOKIE")"
[ "$CODE" = "204" ] || fail "Alice logout returned $CODE"

CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${ALICE_EMAIL}" \
    --data-urlencode "password=${ALICE_PASSWORD}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "Alice re-login returned $CODE"; }
jq -e '.must_complete_first_login == false' "$BODY" >/dev/null \
    || fail "Alice re-login must_complete_first_login is not false"
log "Alice re-login OK with the new password (proves change-password)"

# Wrong passphrase must fail typed on the unwrap path (EC2's negative case);
# set-passphrase is first-time only, so a repeat is 409 already-set.
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/set-passphrase" \
    -H "Authorization: Bearer ${ALICE_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$SP_BODY")"
[ "$CODE" = "409" ] || fail "repeat set-passphrase returned $CODE (want 409)"
jq -e '.type == "/errors/passphrase-already-set"' "$BODY" >/dev/null \
    || fail "repeat set-passphrase type is not /errors/passphrase-already-set"
log "repeat set-passphrase -> 409 /errors/passphrase-already-set"

# --- step 12b: audit-row assertions (after logout + re-login, per the task's
# canonical flow — 'logout' and Alice's 'login_success' only exist now) -----
AUDIT_ACTIONS="$(compose exec -T postgres sh -c '
    PGPASSWORD="$(cat /run/secrets/postgres_password)"
    export PGPASSWORD
    exec psql -h 127.0.0.1 -U emailagent -d emailagent -tA -v ON_ERROR_STOP=1
' <<SQL
SELECT string_agg(action, ',' ORDER BY id)
FROM audit_log
WHERE target_id = '${ALICE_ID}' OR actor_user_id = '${ALICE_ID}';
SQL
)"
AUDIT_ACTIONS="$(printf '%s' "$AUDIT_ACTIONS" | tr -d '\r\n')"
for a in admin_create_user admin_otp_issued otp_login_success change_password set_passphrase logout login_success; do
    printf '%s' ",${AUDIT_ACTIONS}," | grep -q ",${a}," \
        || fail "audit log missing action '${a}' (have: ${AUDIT_ACTIONS})"
done
BAD_HMAC="$(compose exec -T postgres sh -c '
    PGPASSWORD="$(cat /run/secrets/postgres_password)"
    export PGPASSWORD
    exec psql -h 127.0.0.1 -U emailagent -d emailagent -tA -v ON_ERROR_STOP=1
' <<SQL
SELECT count(*) FROM audit_log WHERE octet_length(hmac) IS DISTINCT FROM 32;
SQL
)"
BAD_HMAC="$(printf '%s' "$BAD_HMAC" | tr -d '\r\n')"
[ "$BAD_HMAC" = "0" ] || fail "$BAD_HMAC audit rows have a non-32-byte hmac"
log "audit rows present for every required action; all hmacs are 32 bytes"

# --- step 13: tools/AuditVerify against the live DB -------------------------
STEP=13
log "running tools/AuditVerify against live Postgres"
AUDIT_KEY="$(grep '^AUDIT__HmacKey=' .env | cut -d= -f2-)"
CONN="$(grep '^ConnectionStrings__Postgres=' .env | cut -d= -f2-)"
# Run on the compose network so `Host=postgres` (the §17 connection string
# in .env) resolves — postgres is NOT published to the host. The tree is
# mounted read-write (no --no-build) so a fresh checkout restores and builds
# the tool itself: "exits 0 on a fresh checkout" must not depend on a prior
# solution build existing under tools/AuditVerify/bin.
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
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet run --project tools/AuditVerify \
    > "$ARTIFACT_DIR/audit-verify.log" 2>&1
AV_RC=$?
set -e
tail -n 5 "$ARTIFACT_DIR/audit-verify.log" || true
[ "$AV_RC" = "0" ] || fail "tools/AuditVerify exited $AV_RC (see $ARTIFACT_DIR/audit-verify.log)"
log "tools/AuditVerify -> exit 0 (chain valid)"

# --- step 14: refresh-reuse flow --------------------------------------------
STEP=14
log "refresh-reuse flow"
# Fresh login (admin cookie was rotated/cleared above).
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${ADMIN_EMAIL}" \
    --data-urlencode "password=${ADMIN_PASSWORD}")"
[ "$CODE" = "200" ] || fail "reuse-flow login returned $CODE"
C1="$(refresh_cookie_from_headers "$HDRS")" || fail "no cookie on reuse-flow login"

CODE="$(post_with_cookie "${BASE_URL}/v1/account/refresh" "$C1")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "refresh #1 returned $CODE"; }
C2="$(refresh_cookie_from_headers "$HDRS")" || fail "refresh #1 set no new cookie"
[ "$C2" != "$C1" ] || fail "refresh cookie did not rotate"

CODE="$(post_with_cookie "${BASE_URL}/v1/account/refresh" "$C2")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "refresh #2 returned $CODE"; }
C3="$(refresh_cookie_from_headers "$HDRS")" || fail "refresh #2 set no new cookie"
[ "$C3" != "$C2" ] || fail "refresh cookie did not rotate again"

CODE="$(post_with_cookie "${BASE_URL}/v1/account/refresh" "$C1")"
[ "$CODE" = "401" ] || { cat "$BODY" >&2; fail "rotated-out cookie returned $CODE (want 401)"; }
jq -e '.type == "/errors/unauthorized"' "$BODY" >/dev/null \
    || fail "reuse 401 body is not /errors/unauthorized"
REUSE_COUNT="$(compose exec -T postgres sh -c '
    PGPASSWORD="$(cat /run/secrets/postgres_password)"
    export PGPASSWORD
    exec psql -h 127.0.0.1 -U emailagent -d emailagent -tA -v ON_ERROR_STOP=1
' <<SQL
SELECT count(*) FROM audit_log WHERE action = 'refresh_reuse_detected';
SQL
)"
REUSE_COUNT="$(printf '%s' "$REUSE_COUNT" | tr -d '\r\n')"
[ "$REUSE_COUNT" -ge 1 ] || fail "no refresh_reuse_detected audit row"
UNREVOKED="$(compose exec -T postgres sh -c '
    PGPASSWORD="$(cat /run/secrets/postgres_password)"
    export PGPASSWORD
    exec psql -h 127.0.0.1 -U emailagent -d emailagent -tA -v ON_ERROR_STOP=1
' <<SQL
SELECT count(*) FROM refresh_tokens r
WHERE r.family_id = (
    SELECT family_id FROM refresh_tokens
    WHERE user_id = (SELECT id FROM users WHERE email = '${ADMIN_EMAIL}')
    ORDER BY issued_at DESC LIMIT 1
)
AND r.revoked_at IS NULL;
SQL
)"
UNREVOKED="$(printf '%s' "$UNREVOKED" | tr -d '\r\n')"
[ "$UNREVOKED" = "0" ] || fail "$UNREVOKED refresh_tokens rows in the family are not revoked"
log "refresh-reuse flow OK (401 + refresh_reuse_detected + family revoked)"

# --- step 15: rate-limit flow (6th login = 429) -----------------------------
STEP=15
log "rate-limit flow (6 logins, same IP, one minute)"
# Q-A13: the API honours X-Forwarded-For (ForwardedHeaders), so the per-IP
# partition keys on the client IP nginx observed — this host, for every curl
# below. All proxied calls therefore share ONE bucket, identical to every
# other client behind nginx in this stack. The per-account budget is
# separate (the partition keys on `email`, so a dedicated throwaway account
# below isolates the per-account half too).
RL_EMAIL="ratelimit-${RANDOM}-${RANDOM}@example.com"
# The per-IP partition of the global limiter is a FixedWindowRateLimiter
# seeded at the partition's FIRST login-path request (the admin login in
# step 5 — NOT epoch-aligned), and every proxied request shares the api
# container's RemoteIpAddress. The earlier flows consumed that budget, so
# wait out one full window… but a bare sleep does not phase-align the burst:
# if the six calls straddle the anchored boundary, the 6th returns 401
# instead of 429. Restarting api reseeds the partition so the burst's own
# first request anchors a fresh 60 s window — the bash equivalent of
# AccountTestHost.WaitForFreshRateLimitWindowAsync + a unique test IP.
sleep 65
log "restarting api so the per-IP login window re-anchors on this burst"
compose restart api >/dev/null 2>&1
poll_code "${BASE_URL}/health/ready" 200 120 \
    || fail "/health/ready never returned 200 after rate-limit api restart"
for i in 1 2 3 4 5; do
    CODE="$(curl -sS -o /dev/null -w '%{http_code}' \
        -X POST "${BASE_URL}/v1/account/login" \
        -H 'Content-Type: application/x-www-form-urlencoded' \
        --data-urlencode "email=${RL_EMAIL}" \
        --data-urlencode "password=WrongPassword1!")"
    [ "$CODE" != "429" ] || fail "attempt $i returned 429 (want non-429 within budget)"
    [ "$CODE" = "401" ] || fail "attempt $i returned $CODE (want 401 wrong password)"
done
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${RL_EMAIL}" \
    --data-urlencode "password=WrongPassword1!")"
[ "$CODE" = "429" ] || { cat "$BODY" >&2; fail "6th login returned $CODE (want 429)"; }
grep -qi '^retry-after:' "$HDRS" || fail "429 response has no Retry-After header"
jq -e '.type == "/errors/rate-limited"' "$BODY" >/dev/null \
    || fail "429 body is not /errors/rate-limited"
log "rate-limit flow OK (6th login 429 + Retry-After)"

# --- step 16: AuditVerify again (chain still valid after all flows) --------
STEP=16
set +e
# shellcheck disable=SC2086 # NUGET_RO is intentionally word-split
docker run --rm \
    --network "${PROJECT}_default" \
    -v "$ROOT_DIR:/work" \
    -w /work \
    $NUGET_RO \
    -e ConnectionStrings__Postgres="$CONN" \
    -e AUDIT__HmacKey="$AUDIT_KEY" \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet run --project tools/AuditVerify \
    > "$ARTIFACT_DIR/audit-verify-final.log" 2>&1
AV_RC=$?
set -e
tail -n 3 "$ARTIFACT_DIR/audit-verify-final.log" || true
[ "$AV_RC" = "0" ] || fail "final tools/AuditVerify exited $AV_RC"
log "final tools/AuditVerify -> exit 0"

# --- teardown ---------------------------------------------------------------
log "teardown"
# -v: same reasoning as step 1 — leave no database behind so the next run
# (and the seed refusal it would otherwise hit) starts clean.
compose down -v --remove-orphans >/dev/null 2>&1 || true

echo "OK: all M1 smoke tests passed"