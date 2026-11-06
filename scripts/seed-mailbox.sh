#!/usr/bin/env bash
# M2T7 — seed the dev Greenmail mailbox with the three M2 smoke messages.
#
# Purges every mailbox first (scripts/smoke-m2.sh asserts an EXACT count of
# 3 processed rows after fetch-now), provisions the test user if needed, then
# injects three RFC-822 messages with subjects A, B and C through Greenmail's
# SMTP endpoint.
#
# Seeding path note (verified against the pinned greenmail/standalone:2.0.1
# image): the REST API exposes NO mail inject/read endpoint in this build
# (only configuration, user CRUD, readiness/reset and /api/mail/purge), so
# SMTP is the seeding path — the task file's REST-inject snippet cannot work
# against this image. The user provisioned here (alice/alice) is test-only
# material inside the dev compose project.
#
# Env (defaults match docker/docker-compose.dev.yml):
#   GREENMAIL_REST    REST base URL on the host (default http://localhost:18080)
#   GREENMAIL_SMTP    SMTP endpoint on the host (default localhost:3025)
#   GREENMAIL_USER    test mailbox login         (default alice)
#   GREENMAIL_PASS    test mailbox password      (default alice)
#   GREENMAIL_EMAIL   test mailbox address       (default alice@greenmail.test)
#
# Usage: bash scripts/seed-mailbox.sh   (also invoked by scripts/smoke-m2.sh)
set -euo pipefail

REST="${GREENMAIL_REST:-http://localhost:18080}"
SMTP="${GREENMAIL_SMTP:-localhost:3025}"
MAIL_USER="${GREENMAIL_USER:-alice}"
MAIL_PASS="${GREENMAIL_PASS:-alice}"
MAIL_EMAIL="${GREENMAIL_EMAIL:-alice@greenmail.test}"

log()  { printf '[seed-mailbox] %s\n' "$*"; }
fail() { printf '[seed-mailbox] FAIL: %s\n' "$*" >&2; exit 1; }

command -v curl >/dev/null 2>&1 || fail "required tool 'curl' not found"
command -v jq   >/dev/null 2>&1 || fail "required tool 'jq' not found"

# The mailbox address is interpolated into RFC-822 headers below — a newline
# or header separator in it would inject extra headers into the seeded
# message (the same "review N3" rule GreenMailFixture.CreateConfig follows
# by serializing instead of interpolating).
case "$MAIL_EMAIL" in
    *$'\n'* | *$'\r'* | *'<'* | *'>'*)
        fail "GREENMAIL_EMAIL must not contain newlines or angle brackets (got: ${MAIL_EMAIL})"
        ;;
esac

# --- 1. wait for Greenmail's REST API ---------------------------------------
# Readiness answers 200 without auth once the JVM services are up; poll
# generously so the script works right after `compose up`.
ready=0
for _ in $(seq 1 60); do
    if curl -sf -o /dev/null "$REST/api/service/readiness" 2>/dev/null; then
        ready=1
        break
    fi
    sleep 1
done
[ "$ready" = "1" ] || fail "Greenmail REST at $REST not ready after 60s"
log "Greenmail REST ready ($REST)"

# --- 2. provision the test user ---------------------------------------------
# A duplicate login answers HTTP 500 with an empty body in this image —
# "already exists", not a crash; 200/201 is the fresh-creation answer.
# The body is BUILT BY jq (not shell interpolation) so a password holding a
# quote or backslash cannot break — or steer — the JSON (review N3 rule).
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$REST/api/user" \
    -H 'Content-Type: application/json' \
    --data "$(jq -nc --arg login "$MAIL_USER" --arg password "$MAIL_PASS" --arg email "$MAIL_EMAIL" \
        '{login: $login, password: $password, email: $email}')")"
case "$code" in
    200 | 201 | 500)
        log "user ${MAIL_USER} present (POST /api/user answered $code)"
        ;;
    *)
        fail "POST /api/user answered $code"
        ;;
esac

# --- 3. purge every mailbox --------------------------------------------------
# Empty body, exactly like GreenMailFixture.PurgeMailAsync: Greenmail answers
# 200 and empties all mailboxes (UIDNEXT/UIDVALIDITY intentionally survive).
curl -sf -X POST "$REST/api/mail/purge" -o /dev/null \
    || fail "POST /api/mail/purge failed"
log "mailboxes purged"

# --- 4. inject the three smoke messages over SMTP ----------------------------
# Authenticated as the mailbox owner; a refused send (5xx, e.g. bad password)
# makes curl exit non-zero and `set -e` stops here with the message named.
workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT

for subject in A B C; do
    message="$workdir/msg-${subject}.txt"
    {
        printf 'From: sender@corp.test\n'
        printf 'To: %s\n' "$MAIL_EMAIL"
        printf 'Subject: %s\n' "$subject"
        printf 'Date: %s\n' "$(date -R)"
        printf 'Message-ID: <m2-smoke-%s-%s@localhost>\n' "$subject" "$$"
        printf 'MIME-Version: 1.0\n'
        printf '\n'
        printf 'M2 smoke body %s\n' "$subject"
    } > "$message"

    curl -sS --max-time 15 "smtp://${SMTP}" \
        --mail-from "$MAIL_EMAIL" \
        --mail-rcpt "$MAIL_EMAIL" \
        -u "${MAIL_USER}:${MAIL_PASS}" \
        --upload-file "$message" >/dev/null \
        || fail "SMTP send of subject '${subject}' to ${SMTP} failed"
    log "sent subject '${subject}'"
done

log "seeded 3 messages (subjects A, B, C) into ${MAIL_EMAIL}"
