#!/usr/bin/env bash
# M1T1 — first-admin bootstrap for the dev compose stack.
# (SQ-7 / Q-O11; M1T1 task step 9. See plans/implementation/M1T1 and the
# SQ-7 entry in plans/implementation/M1-identity-encryption-and-data-model.md.)
#
# Inserts the initial admin row via `psql` inside the postgres container,
# authenticated with the postgres Docker secret. Requires a running dev stack
# (`docker compose up`) and `openssl`; run it as `bash scripts/seed-dev.sh`.
# The compose project must match the running stack — the standard
# COMPOSE_PROJECT_NAME variable is honoured when the stack was started with
# an explicit `-p`.
#
# Flow notes (intentional, per the task):
#   * The OTP flow is bypassed — no admin exists yet to issue one (SQ-7).
#   * No DEK / key material is generated: kdf_salt, kdf_params, dek_wrapped
#     and dek_nonce stay NULL until the admin completes
#     POST /v1/account/set-passphrase (Q-E6).
#   * must_change_password=true, so the admin's first login returns
#     must_complete_first_login and the forced first-login change-password
#     path applies (M1T1 step 9; Q-A4).
#   * password_hash is an Identity v3 hash (PBKDF2-HMAC-SHA512, 100k
#     iterations, 16-byte salt, 32-byte subkey — the shipped aspnetcore 10.0.x
#     PasswordHasher defaults M1T4 will verify against), computed here with
#     openssl and assembled in SQL.
#   * The Identity role rows (`Admin` / `User`) and the admin's role
#     membership are provisioned here as well (M1T7): /v1/admin/* is
#     [Authorize(Roles = "Admin")] and the JWT `role` claim comes from these
#     tables (M1T4/SQ-10), so without the membership link the bootstrap
#     admin's tokens would carry no role claim and every admin API call
#     would be a 403.
#
# The generated password is printed ONCE to stdout and never persisted.
# The script refuses to run if the admin already exists.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ADMIN_EMAIL="${BOOTSTRAP__AdminEmail:-admin@local}"

for tool in openssl docker; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "seed-dev: required tool '$tool' not found on PATH" >&2
        exit 1
    fi
done

if [ -z "$(docker compose ps -q postgres 2>/dev/null)" ]; then
    echo "seed-dev: no 'postgres' service is running for this compose project." >&2
    echo "          Start the dev stack first: docker compose up -d" >&2
    exit 1
fi

ADMIN_PASSWORD="${BOOTSTRAP__AdminPassword:-}"
if [ -z "$ADMIN_PASSWORD" ]; then
    # Task step 9 recipe. All writes are smaller than PIPE_BUF, so the
    # head early-exit cannot SIGPIPE the writers under `set -o pipefail`.
    ADMIN_PASSWORD="$(openssl rand -base64 24 | tr -d '=' | head -c 24)"
fi

# `openssl kdf -kdfopt pass:...` splits option name and value on the first
# colon; reject passwords that contain one rather than deriving a wrong hash.
case "$ADMIN_PASSWORD" in
    *:*)
        echo "seed-dev: BOOTSTRAP__AdminPassword must not contain ':'" >&2
        exit 1
        ;;
esac

# Identity v3 hash, byte-identical to what the shipped Microsoft.AspNetCore
# Identity PasswordHasher produces (aspnetcore 10.0.x `HashPasswordV3`):
#   0x01 | uint32 BE prf (2 = HMAC-SHA512 — the shipped V3 default) | uint32 BE
#   iteration count (100000) | uint32 BE salt length (16) | salt (16 bytes)
#   | subkey (32 bytes).
# Note: there is no embedded subkey-length field — the verifier treats
# everything after salt as the subkey. PBKDF2-HMAC-SHA512, 100k iterations,
# 32-byte subkey = the V3 defaults the M1T4 verifier expects (a SHA256-prf
# hash would verify but always report SuccessRehashNeeded).
SALT_HEX="$(openssl rand -hex 16)"
SUBKEY_HEX="$(openssl kdf -keylen 32 \
    -kdfopt digest:SHA512 \
    -kdfopt "pass:${ADMIN_PASSWORD}" \
    -kdfopt "hexsalt:${SALT_HEX}" \
    -kdfopt iter:100000 \
    PBKDF2 | tr -d ':\n')"
HASH_HEX="$(printf '01%08x%08x%08x' 2 100000 16)${SALT_HEX}${SUBKEY_HEX}"

run_psql() {
    # Reads SQL on stdin; extra args are forwarded to psql (typically
    # -v name=value bindings for :'name' interpolation). Authentication uses
    # the postgres Docker secret over the loopback TCP entry in pg_hba.
    docker compose exec -T postgres sh -c '
        PGPASSWORD="$(cat /run/secrets/postgres_password)"
        export PGPASSWORD
        exec psql -h 127.0.0.1 -U emailagent -d emailagent -v ON_ERROR_STOP=1 -tA "$@"
    ' seed-dev "$@"
}

# 1. The users table only exists after the M1T2 migration — fail with a
#    pointed message instead of a raw psql error.
TABLE_STATE="$(printf '%s' \
    "SELECT CASE WHEN to_regclass('public.users') IS NULL THEN 'missing' ELSE 'present' END;" \
    | run_psql)"
if [ "$TABLE_STATE" != "present" ]; then
    echo "seed-dev: table 'users' does not exist yet." >&2
    echo "          Apply the M1T2 migration first (dotnet ef database update)," >&2
    echo "          then re-run this script." >&2
    exit 1
fi

# 2. Identity role-row SQL (M1T7) — defined once so both the fresh-insert
#    path below and the already-exists refusal path can run it: a re-run
#    self-heals a pre-M1T7 database (missing role rows / admin membership).
ROLES_SQL="$(cat <<'SQL'
INSERT INTO "AspNetRoles" (id, name, normalized_name)
VALUES
    (gen_random_uuid(), 'Admin', 'ADMIN'),
    (gen_random_uuid(), 'User', 'USER')
ON CONFLICT (normalized_name) DO NOTHING;

INSERT INTO "AspNetUserRoles" (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN "AspNetRoles" r ON r.normalized_name = 'ADMIN'
WHERE u.email = :'admin_email'
ON CONFLICT (user_id, role_id) DO NOTHING;
SQL
)"

# 3. Refuse to re-run when the admin already exists — after self-healing
#    what later milestones added (M1T7 review): backfill the Identity
#    lookup columns a pre-M1T7 INSERT left NULL (login resolves through
#    normalized_email, so such an admin could never sign in) and ensure the
#    role rows + membership the admin API requires. Both statements are
#    idempotent — a healthy row is untouched.
ADMIN_STATE="$(printf '%s' \
    "SELECT CASE WHEN count(*) > 0 THEN 'exists' ELSE 'absent' END FROM users WHERE email = :'admin_email';" \
    | run_psql -v "admin_email=${ADMIN_EMAIL}")"
if [ "$ADMIN_STATE" = "exists" ]; then
    printf '%s\n' \
        "UPDATE users SET user_name = email, normalized_email = upper(email), normalized_user_name = upper(email) WHERE email = :'admin_email' AND normalized_email IS NULL;" \
        | run_psql -v "admin_email=${ADMIN_EMAIL}"

    printf '%s\n' "$ROLES_SQL" | run_psql -v "admin_email=${ADMIN_EMAIL}"

    echo "seed-dev: admin '${ADMIN_EMAIL}' already exists — refusing to re-run." >&2
    echo "          (Identity lookup columns and the Admin role membership were" >&2
    echo "          backfilled if a pre-M1T7 run had left them missing.)" >&2
    echo "          The initial password was printed only at the first run and" >&2
    echo "          is not recoverable; use POST /v1/admin/users/{id}/otp to" >&2
    echo "          issue a new one-time password instead." >&2
    exit 1
fi

# 4. Insert the admin. Key-material columns are intentionally omitted (NULL).
#    The Identity lookup columns are populated here (M1T7 review): login and
#    otp-login resolve through UserManager.FindByEmailAsync, which matches on
#    normalized_email — an admin row without user_name / normalized_* can
#    never sign in, which would also strand the role membership added below.
INSERT_SQL="$(cat <<'SQL'
INSERT INTO users (
    id, email, user_name, normalized_email, normalized_user_name,
    email_confirmed, role, status, password_hash, must_change_password,
    created_at
)
VALUES (
    gen_random_uuid(),
    :'admin_email',
    :'admin_email',
    upper(:'admin_email'),
    upper(:'admin_email'),
    true,
    'Admin',
    'Active',
    encode(decode(:'hash_hex', 'hex'), 'base64'),
    true,
    now()
)
SQL
)"

printf '%s\n' "$INSERT_SQL" | run_psql \
    -v "admin_email=${ADMIN_EMAIL}" \
    -v "hash_hex=${HASH_HEX}"

# 5. Role rows + the fresh admin's membership (ROLES_SQL from step 2; the
#    admin namespace enforces role=Admin and JwtTokenIssuer builds the
#    `role` claim from these tables — M1T4/SQ-10). Idempotent.
printf '%s\n' "$ROLES_SQL" | run_psql -v "admin_email=${ADMIN_EMAIL}"

cat <<EOF
seed-dev: first admin created.

  email:    ${ADMIN_EMAIL}
  password: ${ADMIN_PASSWORD}

The password above is printed once and never stored anywhere. Log in with
it; must_change_password=true forces the first-login password change, then
set the encryption passphrase (the DEK columns stay NULL until then).
EOF
