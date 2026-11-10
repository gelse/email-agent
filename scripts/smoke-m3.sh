#!/usr/bin/env bash
# M3T7 — scripted M3 sign-off smoke against the dev compose stack.
#
# The M3 exit-criteria command (milestone Verification strategy + task
# scripts/smoke-m3.sh contract): clean stack → two OpenAI mock instances
# (probe-pass / probe-fail) reachable from the api container → migrations →
# seed admin → admin login → flow 1 add the passing model (probe Pass inline,
# row appears in the list, GET by id carries no api_key) → flow 2 add the
# failing model (400 /errors/model-probe-failed, no row written,
# model_probe_failed audit row with target_id IS NULL) → PATCH rename (emits
# update_model) → flow 3 test-completion on the happy scenario (response text,
# 5/2/7 tokens, derived cost, prompt_sha256 audit row WITHOUT the prompt text)
# → flow 4 the user stub picker surface (GET /v1/user/models: reduced DTO,
# allow-list filter, User + Admin roles, anonymous 401) → flow 5 delete the
# passing model (204) and the never-inserted sentinel (404), catalog and
# model_api_key blobs at 0 → the five M3 audit actions with 32-byte HMACs →
# tools/AuditVerify exit 0.
#
# Exit 0 = every step passed ("OK: all M3 smoke tests passed"); a non-zero
# exit names the failing step number and dumps a sanitized diagnostics file
# (artifacts/m3t7/smoke-failure.log) before teardown.
#
# Adaptations vs the task file (recorded in the M3T7 task file):
#   * The mock instances run as NAMED CONTAINERS attached to the compose
#     network (aliases openaimock-a / openaimock-b), started AFTER
#     `compose up` because that is when the project network exists, and also
#     published on 127.0.0.1:9090/9091 for the host-side control endpoint.
#     The task's `dotnet run --urls=http://localhost:9090` is host-process
#     topology: inside the api container `localhost` IS the api container, so
#     the stored base_url is http://openaimock-a:9090/v1 instead of the
#     task's http://localhost:9090. Engine and probe behaviour is unchanged
#     (POST {base_url}/chat/completions, no scenario header, wildcard egress).
#   * The mock is pre-published by a throwaway SDK container
#     (`dotnet publish tests/.../Mocks/OpenAIMock`), so the smoke needs no
#     host dotnet SDK — the documented M3T7 risk "in-process mock vs
#     container" boundary, with the mock code itself untouched.
#   * The scenario control body is UNQUOTED (`--data probe-pass`): the mock
#     stores `ReadToEndAsync().Trim()` verbatim, so the task's `-d '"happy"'`
#     would store `"happy"` and answer 400 unknown scenario.
#   * An explicit `PATCH /v1/admin/models/{id}` rename step was added: the
#     task's five flows never PATCH a model, yet `update_model` is one of the
#     five audit actions the sign-off must find in the chain.
#   * Flow 4 additionally reads GET /v1/user/models as the ADMIN (acceptance
#     criterion: the endpoint serves User AND Admin role callers).
#
# Usage: bash scripts/smoke-m3.sh
# Env:   .env must exist (cp .env.example .env and fill throwaway values).
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

PROJECT="emailagent-m3-smoke"
ARTIFACT_DIR="artifacts/m3t7"
mkdir -p "$ARTIFACT_DIR"

MOCK_NETWORK="${PROJECT}_default"
MOCK_A_NAME="${PROJECT}-mock-a"
MOCK_B_NAME="${PROJECT}-mock-b"
MOCK_A_ALIAS="openaimock-a"
MOCK_B_ALIAS="openaimock-b"
MOCK_A_CONTROL="http://127.0.0.1:9090"
MOCK_B_CONTROL="http://127.0.0.1:9091"
MODEL_BASE_A="http://${MOCK_A_ALIAS}:9090/v1"
MODEL_BASE_B="http://${MOCK_B_ALIAS}:9091/v1"
MOCK_PUBLISH_DIR="${ARTIFACT_DIR}/openai-mock"
MOCK_DLL="/work/${MOCK_PUBLISH_DIR}/OpenAIMock.dll"

log()  { printf '[m3t7] %s\n' "$*"; }
fail() { printf '[m3t7] FAIL (step %s): %s\n' "${STEP:-0}" "$*" >&2; exit 1; }

for tool in docker curl jq openssl; do
    command -v "$tool" >/dev/null 2>&1 || fail "required tool '$tool' not found"
done

[ -f .env ] || fail ".env missing — cp .env.example .env and fill throwaway values"

for k in POSTGRES_PASSWORD AUTH__SigningKey system_kek AUDIT__HmacKey PLUGIN__SigningKey ConnectionStrings__Postgres; do
    grep -Eq "^${k}=." .env || fail ".env has no value for $k"
done

# --- failure diagnostics (pattern: scripts/smoke-m2.sh, M2T7) ----------------
# On any non-zero exit, dump a sanitized snapshot of THIS project to
# artifacts/m3t7/smoke-failure.log while the stack is still up (the inline
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
        echo "=== m3t7 smoke failure diagnostics ==="
        echo "timestamp_utc: $(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || true)"
        echo "exit_status: ${status}"
        echo "step: ${STEP:-unknown}"
        echo "base_url: ${BASE_URL:-unset}"
        echo "compose_project: ${PROJECT}"
        echo
        echo "--- container state (whitelisted fields only) ---"
        local cid
        for cid in $(docker ps -aq --filter "name=${PROJECT}" 2>/dev/null || true); do
            docker inspect --format 'name={{.Name}} status={{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}} error={{.State.Error}} restarts={{.RestartCount}} image={{.Config.Image}}' "$cid" 2>&1 || true
        done
        echo
        echo "--- compose logs (tail 200) ---"
        local svc
        for svc in api postgres web; do
            echo "##### service: ${svc} #####"
            compose logs --no-color --tail=200 "$svc" 2>&1 || true
        done
        echo
        echo "--- openai mock logs (tail 200) ---"
        local mock
        for mock in "$MOCK_A_NAME" "$MOCK_B_NAME"; do
            echo "##### mock: ${mock} #####"
            docker logs --tail 200 "$mock" 2>&1 || true
        done
    } >> "$FAILURE_LOG" 2>&1 || true
    echo "M3 SMOKE FAILED (exit ${status}); diagnostics: ${FAILURE_LOG}" >&2 || true
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
    # Credential-bearing artifacts never survive the run (M2T7 review S2
    # parity) — the Set-Cookie capture, the seeded admin's password output and
    # the model create bodies (they carry the api_key). The diagnostics dump
    # above never reads these files (whitelisted fields only).
    rm -f "${HDRS:-}" "${SEED_OUT:-}" "${CREATE_PASS_BODY:-}" "${CREATE_FAIL_BODY:-}" \
        2>/dev/null || true
    exit "$status"
}
trap on_exit EXIT # THE exit trap: failure diagnostics + temp-file cleanup
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

compose() { docker compose -p "$PROJECT" "$@"; }

# Q-A13: every API call goes through the dev TLS edge (self-signed throwaway
# cert) — same posture as smoke-m1.sh / smoke-m2.sh; trust verification is
# skipped uniformly. The mock control calls are plain http://127.0.0.1, where
# --insecure is inert.
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

# Host-side mock control poll: POST /__test/scenario with the instance's own
# scenario — idempotent (the poll IS the desired state) and it doubles as the
# readiness probe for the port mapping the flow-3 switch uses.
poll_control() {
    # base_url scenario deadline_seconds
    local url="$1" scenario="$2" deadline="$3"
    local end=$((SECONDS + deadline)) code
    while [ "$SECONDS" -lt "$end" ]; do
        code="$(curl -s -o /dev/null -w '%{http_code}' -X POST \
            "${url}/__test/scenario" --data "$scenario" 2>/dev/null || echo 000)"
        [ "$code" = "200" ] && return 0
        sleep 2
    done
    echo "poll: ${url}/__test/scenario last=$code (want 200)" >&2
    return 1
}

# The same control call issued FROM INSIDE the api container: proves the
# alias DNS name resolves and the port is reachable where the engine and the
# probe will call it (the host-side poll above cannot see that path).
poll_control_from_api() {
    # url scenario deadline_seconds
    local url="$1" scenario="$2" deadline="$3"
    local end=$((SECONDS + deadline))
    while [ "$SECONDS" -lt "$end" ]; do
        if compose exec -T api curl -fs -o /dev/null -X POST \
            "${url}/__test/scenario" --data "$scenario" >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    echo "poll from api: ${url}/__test/scenario never answered 2xx" >&2
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
log "removing any leftover mock containers from a previous run"
docker rm -f "$MOCK_A_NAME" "$MOCK_B_NAME" >/dev/null 2>&1 || true
log "compose up (project=$PROJECT)"
# -v: volumes are namespaced to this project — a re-run starts from an empty
# database, so seed-dev.sh never trips over "admin already exists" and the
# five-action audit assertion sees only this run's rows.
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

# --- step 3: publish the OpenAI mock (SDK container, no host dotnet) ---------
STEP=3
log "publishing tests/EmailAgent.IntegrationTests/Mocks/OpenAIMock (Release)"
NUGET_VOL=""
if [ -d /tmp/opencode/nuget ]; then
    NUGET_VOL="-v /tmp/opencode/nuget:/root/.nuget"
fi
# The publish runs as container root, so the result is chowned back to the
# invoking user — a root-owned artifacts/ subdirectory would survive any later
# cleanup of the user-owned parent.
PUBLISH_CMD="dotnet publish tests/EmailAgent.IntegrationTests/Mocks/OpenAIMock -c Release -o '${MOCK_PUBLISH_DIR}' && chown -R $(id -u):$(id -g) '${MOCK_PUBLISH_DIR}'"
# shellcheck disable=SC2086 # NUGET_VOL is intentionally word-split
docker run --rm \
    -v "$ROOT_DIR:/work" \
    -w /work \
    $NUGET_VOL \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    sh -c "$PUBLISH_CMD" \
    > "$ARTIFACT_DIR/publish-openaimock.log" 2>&1 || {
    tail -n 30 "$ARTIFACT_DIR/publish-openaimock.log" >&2
    fail "dotnet publish of the OpenAI mock failed (see $ARTIFACT_DIR/publish-openaimock.log)"
}
[ -f "$MOCK_PUBLISH_DIR/OpenAIMock.dll" ] || fail "publish produced no OpenAIMock.dll"
log "mock published to $MOCK_PUBLISH_DIR"

# --- step 4: start mock A (probe-pass) and mock B (probe-fail) ---------------
# Started AFTER compose up: the containers join ${PROJECT}_default, which
# compose creates at up, and the api container resolves them by alias. The
# fixture directory resolves by walking up from the published dll to the
# mounted repo root (tests/fixtures/openai), hence -v "$ROOT_DIR:/work".
STEP=4
log "starting mock A (${MOCK_A_ALIAS}:9090, scenario probe-pass)"
docker run -d --name "$MOCK_A_NAME" \
    --network "$MOCK_NETWORK" --network-alias "$MOCK_A_ALIAS" \
    -p 127.0.0.1:9090:9090 \
    -v "$ROOT_DIR:/work" -w /work \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet "$MOCK_DLL" --scenario probe-pass --urls http://0.0.0.0:9090 \
    > /dev/null || fail "could not start mock A"
log "starting mock B (${MOCK_B_ALIAS}:9091, scenario probe-fail)"
docker run -d --name "$MOCK_B_NAME" \
    --network "$MOCK_NETWORK" --network-alias "$MOCK_B_ALIAS" \
    -p 127.0.0.1:9091:9091 \
    -v "$ROOT_DIR:/work" -w /work \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet "$MOCK_DLL" --scenario probe-fail --urls http://0.0.0.0:9091 \
    > /dev/null || fail "could not start mock B"
log "polling the host-side control endpoints (readiness)"
poll_control "$MOCK_A_CONTROL" probe-pass 120 \
    || fail "mock A control endpoint never answered on ${MOCK_A_CONTROL}"
poll_control "$MOCK_B_CONTROL" probe-fail 120 \
    || fail "mock B control endpoint never answered on ${MOCK_B_CONTROL}"
log "polling the same endpoints FROM the api container (alias DNS + egress)"
poll_control_from_api "http://${MOCK_A_ALIAS}:9090" probe-pass 60 \
    || fail "mock A is unreachable from the api container via ${MOCK_A_ALIAS}"
poll_control_from_api "http://${MOCK_B_ALIAS}:9091" probe-fail 60 \
    || fail "mock B is unreachable from the api container via ${MOCK_B_ALIAS}"
log "both mocks ready (host control + api-side alias path verified)"

# --- step 5: apply migrations (API does not auto-migrate) --------------------
STEP=5
# The api container runs the SDK-less aspnet:10.0 runtime, so `dotnet ef`
# runs in a throwaway SDK container on the compose network (smoke-m2.sh
# step 4 recipe); the design-time factory reads ConnectionStrings__Postgres.
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

# The api opened its Npgsql pool before the schema existed — restart api/web
# so the process pools attach to the migrated schema (smoke-m1/m2 rationale).
log "restarting api/web so Npgsql sees the migrated schema"
compose restart api web >/dev/null 2>&1
poll_code "${BASE_URL}/health/ready" 200 120 \
    || fail "/health/ready never returned 200 after api restart"

# --- step 6: seed the first admin -------------------------------------------
STEP=6
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

# --- step 7: admin login -----------------------------------------------------
STEP=7
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${ADMIN_EMAIL}" \
    --data-urlencode "password=${ADMIN_PASSWORD}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "admin login returned $CODE"; }
ADMIN_TOKEN="$(jq -r '.access_token // empty' "$BODY")"
[ -n "$ADMIN_TOKEN" ] || fail "admin login body has no access_token"
log "admin login OK"

# --- step 8: flow 1 — admin adds the passing model (exit criterion 1) --------
STEP=8
PASS_MODEL_NAME="test-pass-model"
CREATE_PASS_BODY="$ARTIFACT_DIR/create-model-pass.json"
cat > "$CREATE_PASS_BODY" <<EOF
{"display_name":"m3t7 pass model","provider":"openai-compatible","base_url":"${MODEL_BASE_A}","model_name":"${PASS_MODEL_NAME}","api_key":"sk-test-redacted","price_per_1k_input_usd":0.02,"price_per_1k_output_usd":0.01,"enabled":true}
EOF
chmod 600 "$CREATE_PASS_BODY" # carries the (throwaway) api_key
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/admin/models" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$CREATE_PASS_BODY")"
[ "$CODE" = "201" ] || { cat "$BODY" >&2; fail "create passing model returned $CODE (want 201)"; }
jq -e '.tool_calling_probe_result == "Pass" and .enabled == true and (.id | length > 0)' \
    "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "create passing model must carry tool_calling_probe_result=Pass + id"; }
PASS_ID="$(jq -r '.id // empty' "$BODY")"
[ -n "$PASS_ID" ] || fail "create passing model body has no id"
log "flow 1: passing model ${PASS_ID} created (probe Pass inline)"

# Admin list — the row appears with the Pass verdict.
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    "${BASE_URL}/v1/admin/models?limit=100" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "admin list models returned $CODE"; }
jq -e --arg id "$PASS_ID" \
    'any(.items[]?; .id == $id and .tool_calling_probe_result == "Pass")' \
    "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "admin list does not contain the created model with Pass"; }
log "flow 1: GET /v1/admin/models shows the row (Pass)"

# Read back by id — the response must never echo the key.
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    "${BASE_URL}/v1/admin/models/${PASS_ID}" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "admin get model returned $CODE"; }
jq -e --arg id "$PASS_ID" '(.id == $id) and (has("api_key") | not)' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "GET /v1/admin/models/{id} must answer without an api_key member"; }
log "flow 1: GET by id carries no api_key"

# --- step 9: flow 2 — admin adds the failing model ---------------------------
STEP=9
FAIL_MODEL_NAME="test-fail-model"
CREATE_FAIL_BODY="$ARTIFACT_DIR/create-model-fail.json"
cat > "$CREATE_FAIL_BODY" <<EOF
{"display_name":"m3t7 fail model","provider":"openai-compatible","base_url":"${MODEL_BASE_B}","model_name":"${FAIL_MODEL_NAME}","api_key":"sk-test-redacted","price_per_1k_input_usd":0.02,"price_per_1k_output_usd":0.01,"enabled":true}
EOF
chmod 600 "$CREATE_FAIL_BODY" # carries the (throwaway) api_key
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/admin/models" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$CREATE_FAIL_BODY")"
[ "$CODE" = "400" ] || { cat "$BODY" >&2; fail "create failing model returned $CODE (want 400)"; }
grep -qi '^content-type:.*application/problem+json' "$HDRS" \
    || fail "probe-failed 400 is not application/problem+json"
jq -e '.type == "/errors/model-probe-failed"
       and (.detail | type == "string")
       and (.detail | length > 0)
       and (.detail | length <= 256)' \
    "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "probe failure must be 400 /errors/model-probe-failed with a <=256-byte detail excerpt"; }
log "flow 2: probe fails inline -> 400 /errors/model-probe-failed"

FAIL_ROWS="$(psql_q "SELECT count(*) FROM model_catalog WHERE model_name='${FAIL_MODEL_NAME}';")"
FAIL_ROWS="$(printf '%s' "$FAIL_ROWS" | tr -d '\r\n')"
[ "$FAIL_ROWS" = "0" ] || fail "model_catalog holds ${FAIL_ROWS} rows for the failing model (want 0)"
log "flow 2: no row written for the failing model"

FAIL_AUDIT="$(psql_q "SELECT count(*) FROM audit_log WHERE action='model_probe_failed' AND target_id IS NULL;")"
FAIL_AUDIT="$(printf '%s' "$FAIL_AUDIT" | tr -d '\r\n')"
case "$FAIL_AUDIT" in
    '' | *[!0-9]*) fail "model_probe_failed count is not numeric: '${FAIL_AUDIT}'" ;;
esac
[ "$FAIL_AUDIT" -ge 1 ] || fail "no model_probe_failed audit row with target_id IS NULL"
log "flow 2: model_probe_failed audit row present (target_id IS NULL)"

# --- step 10: PATCH rename (emits update_model — required by the 5 actions) --
STEP=10
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X PATCH "${BASE_URL}/v1/admin/models/${PASS_ID}" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data '{"display_name":"m3t7 pass model renamed"}')"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "PATCH model returned $CODE (want 200)"; }
jq -e '.display_name == "m3t7 pass model renamed"' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "PATCH model response does not carry the new display_name"; }
log "PATCH rename -> 200 (update_model emitted; within-window probe answers PassFromCache)"

# --- step 11: flow 3 — admin runs test-completion (exit criterion 2) ---------
STEP=11
log "switching mock A to the happy scenario (control endpoint, unquoted body)"
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${MOCK_A_CONTROL}/__test/scenario" --data 'happy')"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "mock A scenario switch returned $CODE"; }
jq -e '.scenario == "happy"' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "mock A did not accept the happy scenario"; }

TC_BODY="$ARTIFACT_DIR/test-completion.json"
cat > "$TC_BODY" <<'EOF'
{"system_prompt":"You are helpful.","user_prompt":"Say hello."}
EOF
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/admin/models/${PASS_ID}/test-completion" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}" \
    -H 'Content-Type: application/json' \
    --data-binary "@$TC_BODY")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "test-completion returned $CODE (want 200)"; }
jq -e 'has("response_text") and has("prompt_tokens") and has("completion_tokens")
       and has("total_tokens") and has("cost_usd") and has("duration_ms")
       and .response_text == "hello world"
       and .prompt_tokens == 5
       and .completion_tokens == 2
       and .total_tokens == 7
       and (.cost_usd == 0.00012)
       and ((.duration_ms | type) == "number")' \
    "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "test-completion body must carry response hello world, 5/2/7 tokens, cost 0.00012, duration_ms"; }
log "flow 3: test-completion -> response 'hello world', 5/2/7 tokens, cost 0.00012"

PROMPT_HASH="$(psql_q "SELECT metadata_json->>'prompt_sha256' FROM audit_log WHERE action='test_model_completion' ORDER BY id DESC LIMIT 1;")"
PROMPT_HASH="$(printf '%s' "$PROMPT_HASH" | tr -d '\r\n')"
printf '%s' "$PROMPT_HASH" | grep -Eq '^[0-9a-f]{64}$' \
    || fail "prompt_sha256 is not a 64-char lowercase hex string: '${PROMPT_HASH}'"
PROMPT_LEAK="$(psql_q "SELECT count(*) FROM audit_log WHERE action='test_model_completion' AND metadata_json::text LIKE '%Say hello.%';")"
PROMPT_LEAK="$(printf '%s' "$PROMPT_LEAK" | tr -d '\r\n')"
[ "$PROMPT_LEAK" = "0" ] || fail "test_model_completion metadata contains the prompt text"
log "flow 3: audit row has prompt_sha256 (64 hex) and no prompt text"

# --- step 12: flow 4 — user stub picker (GET /v1/user/models) ----------------
STEP=12
ALICE_EMAIL="alice-m3-${RANDOM}-${RANDOM}@example.com"
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
OTP="$(jq -r '.otp // empty' "$BODY")"
[ -n "$OTP" ] || fail "create-user body missing otp"
log "create user OK (otp captured, not logged)"

CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "${BASE_URL}/v1/account/otp-login" \
    -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "email=${ALICE_EMAIL}" \
    --data-urlencode "otp=${OTP}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "otp-login returned $CODE"; }
ALICE_TOKEN="$(jq -r '.access_token // empty' "$BODY")"
[ -n "$ALICE_TOKEN" ] || fail "otp-login body has no access_token"
log "otp-login OK"

# The user-namespace allow-list: ONLY the passing model, reduced DTO, with the
# §13.14 rate-limit headers on the success path.
CODE="$(curl -sS -D "$HDRS" -o "$BODY" -w '%{http_code}' \
    "${BASE_URL}/v1/user/models?limit=100" \
    -H "Authorization: Bearer ${ALICE_TOKEN}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "GET /v1/user/models as User returned $CODE"; }
grep -qi '^x-ratelimit-limit:' "$HDRS" \
    || fail "GET /v1/user/models 200 carries no X-RateLimit-Limit header"
jq -e --arg id "$PASS_ID" '
    (.items | length) == 1
    and .items[0].id == $id
    and .items[0].display_name == "m3t7 pass model renamed"
    and .items[0].provider == "openai-compatible"
    and .items[0].model_name == "test-pass-model"
    and ((.items[0] | keys) == ["display_name", "id", "model_name", "provider"])
    and ((.items[0] | has("base_url")) | not)
    and ((.items[0] | has("api_key")) | not)' \
    "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "GET /v1/user/models must return ONLY the passing row as the 4-member reduced DTO"; }
log "flow 4: user sees exactly the passing model, reduced DTO (4 members, no base_url/api_key)"

# The same endpoint served to an Admin-role caller (acceptance criterion:
# User AND Admin roles), and rejected for anonymous callers.
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    "${BASE_URL}/v1/user/models?limit=100" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}")"
[ "$CODE" = "200" ] || { cat "$BODY" >&2; fail "GET /v1/user/models as Admin returned $CODE"; }
jq -e --arg id "$PASS_ID" '(.items | length) == 1 and .items[0].id == $id' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "admin read of /v1/user/models disagrees with the user read"; }

CODE="$(curl -sS -o "$BODY" -w '%{http_code}' "${BASE_URL}/v1/user/models?limit=100")"
[ "$CODE" = "401" ] || { cat "$BODY" >&2; fail "anonymous GET /v1/user/models returned $CODE (want 401)"; }
jq -e '.type == "/errors/unauthorized"' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "anonymous 401 type is not /errors/unauthorized"; }
log "flow 4: Admin role allowed, anonymous 401 /errors/unauthorized"

# --- step 13: flow 5 — admin deletes the passing model -----------------------
STEP=13
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X DELETE "${BASE_URL}/v1/admin/models/${PASS_ID}" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}")"
[ "$CODE" = "204" ] || { cat "$BODY" >&2; fail "DELETE model returned $CODE (want 204)"; }
log "flow 5: DELETE passing model -> 204"

# The failing model was never inserted — deleting its id is a 404.
SENTINEL_ID="00000000-0000-0000-0000-0000000000ff"
CODE="$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X DELETE "${BASE_URL}/v1/admin/models/${SENTINEL_ID}" \
    -H "Authorization: Bearer ${ADMIN_TOKEN}")"
[ "$CODE" = "404" ] || { cat "$BODY" >&2; fail "DELETE never-inserted model returned $CODE (want 404)"; }
jq -e '.type == "/errors/model-not-found"' "$BODY" >/dev/null \
    || { cat "$BODY" >&2; fail "DELETE 404 type is not /errors/model-not-found"; }
log "flow 5: DELETE never-inserted id -> 404 /errors/model-not-found"

MODEL_ROWS="$(psql_q "SELECT count(*) FROM model_catalog;")"
MODEL_ROWS="$(printf '%s' "$MODEL_ROWS" | tr -d '\r\n')"
[ "$MODEL_ROWS" = "0" ] || fail "model_catalog has ${MODEL_ROWS} rows left (want 0)"
BLOB_ROWS="$(psql_q "SELECT count(*) FROM encrypted_blobs WHERE purpose='model_api_key';")"
BLOB_ROWS="$(printf '%s' "$BLOB_ROWS" | tr -d '\r\n')"
[ "$BLOB_ROWS" = "0" ] || fail "encrypted_blobs has ${BLOB_ROWS} model_api_key rows left (want 0, cascade)"
log "flow 5: model_catalog=0 and model_api_key blobs=0 (cascaded)"

# --- step 14: the five M3 audit actions are in the chain ---------------------
STEP=14
for a in create_model update_model delete_model model_probe_failed test_model_completion; do
    C="$(psql_q "SELECT count(*) FROM audit_log WHERE action='${a}';")"
    C="$(printf '%s' "$C" | tr -d '\r\n')"
    case "$C" in
        '' | *[!0-9]*) fail "audit count for '${a}' is not numeric: '${C}'" ;;
    esac
    [ "$C" -ge 1 ] || fail "audit log has no '${a}' row after the M3 flow"
done
BAD_HMAC="$(psql_q "SELECT count(*) FROM audit_log WHERE octet_length(hmac) IS DISTINCT FROM 32;")"
BAD_HMAC="$(printf '%s' "$BAD_HMAC" | tr -d '\r\n')"
[ "$BAD_HMAC" = "0" ] || fail "${BAD_HMAC} audit rows have a non-32-byte hmac"
log "all 5 M3 audit actions present; every hmac is 32 bytes"

# --- step 15: tools/AuditVerify against the live DB --------------------------
STEP=15
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
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet run --project tools/AuditVerify \
    > "$ARTIFACT_DIR/audit-verify.log" 2>&1
AV_RC=$?
set -e
tail -n 5 "$ARTIFACT_DIR/audit-verify.log" || true
[ "$AV_RC" = "0" ] || fail "tools/AuditVerify exited $AV_RC (see $ARTIFACT_DIR/audit-verify.log)"
log "tools/AuditVerify -> exit 0 (chain valid through every M3 flow)"

# --- teardown ----------------------------------------------------------------
log "teardown"
docker rm -f "$MOCK_A_NAME" "$MOCK_B_NAME" >/dev/null 2>&1 || true
# -v: leave no database behind so the next run starts clean (m1 contract).
compose down -v --remove-orphans >/dev/null 2>&1 || true

echo "OK: all M3 smoke tests passed"
