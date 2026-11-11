# Sources API (`/v1/user/sources`)

CRUD, connection test, and fetch-now endpoints for a user's mail sources. Introduced in
[M2T5](../../plans/implementation/M2T5-sources-api-endpoints-crud.md); built on the M2T1/M2T3
connector framework (`IMessageSource`, `IPluginRegistry`) and the `SourceService` layer and
`processed_messages` dedupe table introduced by this task. The passphrase → DEK unlock endpoint
(`POST /v1/user/account/unwrap-dek`) and the SPA-side `X-User-Dek` plumbing were added by
[M2T6](../../plans/implementation/M2T6-sources-spa-screens.md) under amendment R2, and both were
superseded by the M5T1 session-key envelope ([M5T1](../../plans/implementation/M5T1-session-key-envelope-transport.md)
removed the `X-User-Dek` header entirely — see the authentication section below).

All `/v1/user/sources*` routes require a bearer token with the `User` role (`RequireRole("User")`)
and act on the **caller's own sources only** — every query filters by `user_id` taken from the
token, never from the request. Since M5T1 (SQ-M5-1) every `/v1/user/*` route additionally requires
the `X-Crypto-Session` header minted by `POST /v1/account/unlock`; the shared
`CryptoSessionMiddleware` answers a valid JWT over a locked vault with `403
/errors/crypto-locked`. The companion `/v1/user/account/*` routes (`kdf`, `unwrap-dek`)
require an authenticated, non-disabled account and likewise resolve the caller's own row from the
token: no role claim is demanded there, and nothing in that namespace reads or writes another
account. Admin routes live under `/v1/admin/*` (see `plans/01-architecture.md` §9).

## Endpoints

| Method | Route | Success | Notes |
|---|---|---|---|
| `GET` | `/v1/account/kdf` | `200 {kdf_salt, kdf_params, dek_wrapped, dek_nonce}` | **M5T1 (SQ-M5-1).** The unlock flow's pre-step: the caller's non-secret key material (Argon2id salt + public parameters, the KEK-wrapped DEK ciphertext and its 12-byte GCM nonce, base64) under `Cache-Control: no-store`. JWT-only — deliberately **not** session-gated (the vault is locked precisely when this is needed). `409 /errors/passphrase-not-set` when no key material exists yet; `403` for a disabled account. Read-only, not audited. |
| `POST` | `/v1/account/unlock` | `200 {crypto_session_id, crypto_session_token}` | **M5T1 (SQ-M5-1).** Body `{"dek": "<base64 of exactly 32 bytes>"}` — the DEK the browser derived/unwrapped client-side; the passphrase never reaches the server. The server wraps the DEK under a fresh random 256-bit session key, holds the entry in the in-process store (RAM only — no table, no migration) and returns the opaque token exactly once under `Cache-Control: no-store`. Idempotent replace: any live session for the user is revoked first (`crypto_session_revoked`, `reason: "replaced"`) — one live session per user. Ordered failures: `401 /errors/unauthorized` (dead account), `403` disabled account, `400 /errors/validation` (missing, malformed, or over-size body — `Content-Length` above 2 KiB is refused up front, and the read itself stops at 2048 characters so a chunked request without a `Content-Length` cannot bypass the cap), `400 /errors/dek-invalid` (`dek` not base64 or not 32 bytes, Q-E9). Audited `crypto_session_opened` after the store write with `metadata_json = {"session_id": …}` only — never the token, never the DEK. Not one of the eight §13.3 M5 user-namespace routes. |
| `GET` | `/v1/user/account/kdf` | `200 {kdf_salt, kdf_params}` | Caller's KDF material (Argon2id over `users.kdf_salt` + `kdf_params`) for tooling that inspects or regenerates KDF parameters. **Superseded by `GET /v1/account/kdf` for the SPA unlock flow (M5T1)** and **session-gated since M5T1** like every other `/v1/user/*` route. `409 /errors/passphrase-not-set` when no passphrase is set yet. Read-only, not audited. |
| `POST` | `/v1/user/account/unwrap-dek` | `200 {dek}` | **Amendment R2 (M2T6); superseded by `POST /v1/account/unlock` (M5T1).** Body `{"passphrase": "…"}` (whole body capped at 4 KiB); the server derives the KEK with Argon2id and unwraps `users.dek_wrapped`, answering with the raw 32-byte DEK as base64 under `Cache-Control: no-store`. The SPA no longer sends passphrases to the server — unlock derives client-side — and the route, retained for compatibility, is session-gated since M5T1. Ordered failures: `401 /errors/unauthorized` when the token's account no longer exists, then `403` disabled account, then `409 /errors/passphrase-not-set` **before the body is read**, then `400 /errors/validation` with `errors[].name = "passphrase"` for a missing, malformed, empty, or over-size body, then `429` + `Retry-After: 1` `/errors/rate-limited` while another unwrap for the same user is in flight, then `401 /errors/credential-mismatch` for a wrong passphrase (audited as `dek_unwrap_failed`; a successful unwrap is not audited). |
| `GET` | `/v1/user/sources` | `200 {items, next_cursor?}` | Keyset cursor pagination; filters `type_id`, `status`. Never 204 — an empty caller has `"items": []`. |
| `POST` | `/v1/user/sources` | `201` + `Location: /v1/user/sources/{id}` | Schema-gates the config against the plugin's `ConfigSchema`; `400 /errors/plugin-not-registered` for an unknown `type_id`. |
| `GET` | `/v1/user/sources/{id}` | `200` | `404 /errors/source-not-found` for other users' ids — **never 403** (no existence oracle). |
| `PATCH` | `/v1/user/sources/{id}` | `200` | Partial update; `interval_minutes` must stay `1..1440`. |
| `DELETE` | `/v1/user/sources/{id}` | `204` | `409 /errors/conflict` while the source still owns `processed_messages` rows (FK `ON DELETE RESTRICT`, Q-O15; relaxation is M6aT7). Session header required (the generic `/v1/user/*` gate) — nothing is decrypted here. |
| `POST` | `/v1/user/sources/test-connection` | `200 {ok, error?}` | **Collection-level**, body `{type_id, config}` — tests a config before it is saved, so no source id exists yet. Always `200` for expected failures; see below. |
| `POST` | `/v1/user/sources/{id}/fetch-now` | `200 {processed, new_processed_messages, checkpoint}` | Manual exerciser (no scheduler in M2): fetches new messages, persists dedupe-keyed rows, advances the checkpoint, audits. |

### Authentication and the `X-Crypto-Session` header (M5T1 session-key envelope)

Source configs are stored encrypted under a **user-owned data encryption key (DEK)**. Since M5T1
(SQ-M5-1) the DEK crosses the wire exactly once — during unlock — and every user-data request is
judged on the opaque session token instead:

1. **Read the key material:** `GET /v1/account/kdf` returns `{kdf_salt, kdf_params, dek_wrapped,
   dek_nonce}` (all non-secret: a salt, public Argon2id parameters, and the KEK-wrapped DEK
   ciphertext the server itself cannot open without the passphrase) under `Cache-Control:
   no-store`.
2. **Unlock:** the browser derives the KEK client-side (Argon2id over `kdf_salt`/`kdf_params`),
   AES-GCM-unwraps `dek_wrapped` to the raw 32-byte DEK and POSTs it **once** to
   `POST /v1/account/unlock` as `{"dek": "<base64>"}`. The passphrase never leaves the browser and
   the server never derives a KEK. The response — `200 { crypto_session_id,
   crypto_session_token }`, again `no-store` — is the only place the token ever appears; the
   server keeps `sha256(token)` plus the DEK wrapped under a fresh random 256-bit session key in
   an in-process `IMemoryCache` store (RAM only: no table, no migration, nothing survives a
   process restart).
3. **Use it:** every user-data request carries `Authorization: Bearer <jwt>` **plus**
   `X-Crypto-Session: <crypto_session_token>`; handlers resolve the DEK from the store through the
   request-scoped accessor. The DEK never traverses the wire again and is never written to the
   database — `users.dek_wrapped` (KEK-wrapped) remains the durable custody form, so an
   administrator holding a database dump still cannot decrypt user data at rest (SQ-M5-1).

The SPA holds the token in memory only — lost on page refresh → the passphrase prompt appears
again (`sessionStorage`/`localStorage` are explicitly not used) — and `CryptoSessionHandler`
attaches the header to `/v1/user/*` calls only:

| Route namespace | `X-Crypto-Session` |
|---|---|
| every `/v1/user/*` route — sources, actions, notifications, models, legacy `/v1/user/account/*` | **required** |
| `/v1/account/*` (login, refresh, logout, `kdf`, `unlock`) | not required — the unlock pair is what creates the session |
| `/v1/admin/*`, `/health*`, `/metrics` | not required (not user-data routes) |

**401 vs 403 (SQ-M5-1):** the two credentials are independently invalidatable, so the failure
modes never collapse. An absent, malformed, or expired JWT is refused first with `401
/errors/unauthorized` (the unchanged bearer challenge). A **valid** JWT whose crypto session is
absent, forged, revoked, or timed out gets RFC 7807 **`403 /errors/crypto-locked`** — title
"Crypto session locked", detail "The data-encryption vault for this session is locked or expired.
Unlock with the account passphrase and retry." — which tells the SPA to re-prompt for the
passphrase rather than log the user out.

**Timeouts** (SQ-M5-6): `SESSION__IdleTimeout` (default `00:30:00`) is sliding — every successful
user-data request refreshes the idle deadline — and is always clamped to
`SESSION__AbsoluteTimeout` (default `12:00:00`), a hard cap that a JWT refresh does **not**
extend. **Revocation cascades** re-lock the vault: cookie-based logout (`reason: "logout"`),
refresh-token family reuse/revocation (§13.11, `reason: "refresh_family_revoked"`), a server
restart (the store is in-process memory — all sessions die), and re-unlock itself (idempotent
replace: `reason: "replaced"`, at most one live session per user). Each outcome writes a
`crypto_session_revoked` audit row; unlock writes `crypto_session_opened` — metadata carries the
session id (and reason) only, never the token or the DEK.

**Accepted risk — the unlock trust model (SQ-M5-1):** the server holds no passphrase verifier and
no unwrapped key material (that is what "no crypto table" buys), so `POST /v1/account/unlock`
cannot check that the caller knows the passphrase — it trusts a bearer-authenticated account that
presents a well-formed 32-byte `dek`. A caller holding only a stolen JWT can therefore mint a
session around an arbitrary key, which evicts the legitimate live session (`reason: "replaced"`,
audited) and forces the real user to re-unlock. The impact is bounded and accepted for v1: the
attacker's session never holds the real DEK, so no existing ciphertext becomes readable, and the
trust model is unchanged from M2–M4, where the same JWT-holder could supply any `X-User-Dek`
header directly. What a stolen JWT does buy is a session that can write under a bogus key
(overwriting encrypted fields its role can reach) plus the re-lock itself — bounded by the §13.14
authenticated rate limits, and audited through `crypto_session_opened` /
`crypto_session_revoked … reason: "replaced"`.

> **Removed (M5T1):** the M2–M4 `X-User-Dek` header and its `errors[].name = "x-user-dek"`
> validation are gone (§13.16 / Q-E9's removal mandate — `DekHeaderHandler` was deleted). A
> user-data request carrying `X-User-Dek` without a valid `X-Crypto-Session` gets `403
> /errors/crypto-locked`, never the config; the header itself is inert. The M2
> `POST /v1/user/account/unwrap-dek` route persists for compatibility but is session-gated like
> every other `/v1/user/*` route and is no longer part of the SPA flow.

### Validation and RFC 7807 problem types

Config validation returns `400` with type `/errors/config-validation` and an `errors[]` array of
`{name, reason}` where `name` is the **wire field name from the plugin JSON schema** (e.g. `port`)
and `reason` the human-readable constraint (e.g. `must be between 1 and 65535`).

| Problem `type` | Status | Cause |
|---|---|---|
| `/errors/unauthorized` | 401 | Anonymous, malformed, or dead-account token — the credential half of the SQ-M5-1 split (contrast `/errors/crypto-locked`, where the JWT is valid). |
| `/errors/forbidden` | 403 | Wrong role, or a disabled account. |
| `/errors/crypto-locked` | 403 | **M5T1 (SQ-M5-1).** Valid JWT + absent/forged/revoked/expired `X-Crypto-Session` on any `/v1/user/*` route — the vault is locked; unlock again with the passphrase. Title "Crypto session locked". Distinct from 401 (credential problem) and from `/errors/forbidden` (role or disabled account). |
| `/errors/validation` | 400 | Request shape, body JSON, cursor, `limit`, or `status` (`errors[].name` = offending member: `config`, `interval_minutes`, `type_id`, `cursor`, `limit`, `status`, `passphrase`, `body`). |
| `/errors/config-validation` | 400 | Config fails the plugin's `ConfigSchema` — raised **before any socket is opened** (e.g. `port: 99999`). |
| `/errors/plugin-not-registered` | 400 | `type_id` not offered by any registered plugin. **M2T7 refusal-wins:** a drop-in candidate refused at load time also removes the `plugin_id` it claims from the registry for that cycle (`IPluginRegistry.UnregisterMessageSource`), so a tampered file claiming `imap` takes even the build-output `imap` offline — `test-connection` then answers this error instead of reaching a plugin whose `plugin_rejected` row says it was refused. Remove the refused file and reload to restore it. |
| `/errors/dek-invalid` | 400 | `POST /v1/account/unlock` body `dek` is not base64 or not exactly 32 bytes (Q-E9). The sources routes also return this type when the session DEK cannot open a stored source config blob — a GCM failure inside the config store (e.g. after a re-unlock minted a session around different key material). Accessor-level failures (locked/expired/absent session) are `403 /errors/crypto-locked` instead, as are all session-DEK failures on the actions routes. |
| `/errors/credential-mismatch` | 401 | `POST /v1/user/account/unwrap-dek` with a wrong passphrase — detail "The passphrase is not valid."; audited as `dek_unwrap_failed`. Distinct from `/errors/unauthorized`, which is a token problem. |
| `/errors/source-not-found` | 404 | Missing id **or another user's id** (indistinguishable by design). |
| `/errors/conflict` | 409 | `DELETE` on a source that still has `processed_messages` rows. |
| `/errors/passphrase-not-set` | 409 | `GET /v1/account/kdf` (or the legacy `POST /v1/user/account/unwrap-dek` / `GET /v1/user/account/kdf`) before the first-login passphrase flow; on the legacy unwrap route it is raised before the body is read. |
| `/errors/rate-limited` | 429 | `unwrap-dek` while another unwrap for the same user is in flight (or the authenticated window is exhausted); carries `Retry-After: 1` on the busy path. |
| `/errors/source-connection-failed` | 500 | The plugin's connection test threw an unclassifiable exception (expected failures never get here). |
| `/errors/internal-blob-missing` | 500 | Source row/blob mismatch (integrity alarm). |
| `/errors/internal` | 500 | Anything untyped. |

**`test-connection` semantics:** expected failures are **values, not HTTP errors** — a refused
login or unreachable host answers `200` with `{ "ok": false, "error": "authentication failed: …" }`
(or `connection failed: …`), which the SPA renders inline. Failures the API can prove at the
boundary (unknown plugin, `port: 99999`) are `400` RFC 7807 documents raised before any network
I/O. Request: `POST /v1/user/sources/test-connection` with `{"type_id": "imap", "config": {...}}`.

Every `4xx`/`5xx` is an `application/problem+json` document with `type`, `title`, `status`,
`detail`, `instance`, and — where applicable — `errors[]`.

### Rate-limit headers

Every authenticated response in this namespace carries `X-RateLimit-Limit`,
`X-RateLimit-Remaining`, and `X-RateLimit-Reset` (per-user fixed window, 60 requests/minute).
Unauthenticated responses carry none of them.

### Pagination

`GET /v1/user/sources?limit=<1..100, default 25>&cursor=<opaque>&type_id=<t>&status=<s>`.

The cursor payload is `base64("{created_at:O}|{id}")` for the last item of the previous page,
transmitted as **base64url** (URL-safe alphabet, no padding) so `next_cursor` can be pasted into
the query string verbatim — the decoder also accepts plain standard base64 for hand-built cursors.
The keyset predicate is `(created_at, id) < (cursor.created_at, cursor.id)` with
`ORDER BY created_at DESC, id DESC LIMIT n+1` (the extra row proves a next page exists). A bad
cursor → `400 /errors/validation` with `errors[].name = "cursor"`; a bad `limit` → `errors[].name
= "limit"`; a bad `status` → `errors[].name = "status"` (`Active`, `Paused`, `Error`).
`next_cursor` is **omitted** on the last page (including the empty one). Rows tied on
`created_at` are ordered by `id`, so pages stay disjoint and complete. If your HTTP client
re-encodes query values anyway (`encodeURIComponent` / `Uri.EscapeDataString`), that is harmless.

### Create / update bodies

```jsonc
POST /v1/user/sources                      // X-Crypto-Session required
{
  "type_id": "imap",                       // must be registered, else 400 /errors/plugin-not-registered
  "display_name": "work imap",
  "config": { "host": "…", "port": 993, "use_ssl": true, "username": "…", "password": "…", "mailbox": "INBOX" },
  "interval_minutes": 5                    // optional, default 5, 1..1440
}
```

The stored row starts `status='Active'`, `checkpoint_json='{}'`, `last_run_at`/`next_run_at` null.

`password` inside `config` lives only **inside the encrypted config document** — an
`encrypted_blobs` row with `purpose='message_source_config'`, wrapped under the user DEK — and
**never appears in any response**: every response is a typed record whose config projection has no
`password` member, so the raw JSON body cannot contain the field either. `PATCH` semantics:

- `display_name` / `interval_minutes` absent (or `interval_minutes: null`) → unchanged;
- `config` absent → stored config untouched;
- `config` present → re-validated, re-encrypted into a **new** blob (old blob deleted in the same
  transaction); `password` **absent or `""` keeps the stored password, any non-empty value
  replaces it** (the API never returns the password, so a form that did not collect it must not
  wipe it).

`created_at` comes back as a UTC ISO-8601 `DateTimeOffset`.

### fetch-now

`POST /v1/user/sources/{id}/fetch-now` (session required) validates the stored config again, iterates
`ListNewMessagesAsync` from the checkpoint, and per new message writes one `processed_messages`
row via literal `INSERT … ON CONFLICT (source_id, remote_uid) DO NOTHING` plus its encrypted body
blob (`purpose='message_body'`, `body_blob_id` per Q-LP1). Rows land with `stage='fetched'`,
`prefilter_matched=false`, and **every engine column NULL** — no LLM/engine is reachable from this
path. Then `AdvanceCheckpointAsync` runs, `checkpoint_json`/`last_run_at`/`next_run_at` are
updated, and a `fetch_source_now` audit row is written — all in one transaction.

Response: `{ "processed": <yielded>, "new_processed_messages": <inserted>, "checkpoint": {"last_uid": …, "extra": {…}} }`.

### Audit trail

Every state change appends an audit row for the caller: `create_source`, `update_source`,
`delete_source`, `test_source_connection`, `fetch_source_now` — all with
`target_type='source'` and `target_id` = the source's id for the four source-addressed actions.
`test_source_connection` runs before a source exists, so its `target_id` is `null` and its
metadata carries only `type_id`/`ok`. No row ever contains a config document or credential.
Reads are not audited. Rows join the same transaction as the data change (`AuditLogger` joins
the ambient transaction), so a rollback never leaves a phantom entry.

The companion `POST /v1/user/account/unwrap-dek` (M2T6) is audited **on failure only**:
`dek_unwrap_failed` with `target_type='user'`, `target_id` = the caller's id, and empty metadata
(`{}`) — never the passphrase, KEK, or DEK. A successful unwrap writes no audit row.

The M5T1 unlock flow is audited through the same chain (SQ-M5-8's spirit): every successful
`POST /v1/account/unlock` appends `crypto_session_opened` with `target_type='crypto_session'`,
`target_id` = the session id and `metadata_json = {"session_id": …}`, **after** the store write;
every revocation appends `crypto_session_revoked` with
`metadata_json = {"session_id": …, "reason": …}` where `reason ∈ logout | refresh_family_revoked |
replaced`. No audit row ever carries the session token or the DEK. `GET /v1/account/kdf` is a read
and is not audited.

> **Known limitation (M2):** the 4 KiB body cap applies to *every* unwrap, including passphrases
> set before M2T6, when `set-passphrase` had no 512-character ceiling. A stored passphrase whose
> JSON-escaped form exceeds the cap can therefore never be unwrapped in M2 — `set-passphrase`
> answers `409 /errors/passphrase-already-set`, and re-wrap is M6a. Recorded rather than
> worked around: no M2 endpoint may rewrite `users.dek_wrapped` without a valid KEK. Accounts set
> through the M2 form are unaffected (512 characters escape to well under 4 KiB).

### Example

```bash
# 1. Unlock (M5T1, SQ-M5-1): read the KDF material, derive the KEK and unwrap the DEK in the
#    browser (Argon2id + AES-GCM — the passphrase never leaves it), then mint the session ONCE.
curl -sS -H "Authorization: Bearer $TOKEN" https://host/v1/account/kdf
#    -> { "kdf_salt": "…", "kdf_params": { "memory_kb": …, "iterations": …, … },
#         "dek_wrapped": "…", "dek_nonce": "…" }
#    $DEK below stands for that client-side unwrap result (base64, exactly 32 bytes).
SESSION=$(curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d "{\"dek\":\"$DEK\"}" https://host/v1/account/unlock | jq -r '.crypto_session_token')
# No passphrase set yet -> 409 /errors/passphrase-not-set on kdf; dek not base64 / not 32 bytes
# -> 400 /errors/dek-invalid; the token is returned under Cache-Control: no-store, exactly once.

# 2. List a page of 2 — every user-data call now carries the session header.
#    (next_cursor is URL-safe and echoed verbatim; absent = last page.)
curl -sS -H "Authorization: Bearer $TOKEN" -H "X-Crypto-Session: $SESSION" \
  "https://host/v1/user/sources?limit=2"
# No/invalid JWT -> 401 /errors/unauthorized; valid JWT but no/forged/expired session ->
# 403 /errors/crypto-locked (re-prompt and unlock again). Then follow the cursor:
# "https://host/v1/user/sources?limit=2&cursor=$NEXT_CURSOR"

# 3. Create, then read back — no password field in either response.
curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "X-Crypto-Session: $SESSION" \
  -H "Content-Type: application/json" \
  -d '{"type_id":"imap","display_name":"home","config":{"host":"imap.local","port":143,"use_ssl":false,"username":"a","password":"p","mailbox":"INBOX"}}' \
  https://host/v1/user/sources

# 4. Test a config before saving it (200 even on failure — read `ok`).
curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "X-Crypto-Session: $SESSION" \
  -H "Content-Type: application/json" \
  -d '{"type_id":"imap","config":{"host":"imap.local","port":143,"use_ssl":false,"username":"a","password":"p","mailbox":"INBOX"}}' \
  https://host/v1/user/sources/test-connection

# 5. Fetch now (manual exerciser; session required).
curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "X-Crypto-Session: $SESSION" \
  https://host/v1/user/sources/$ID/fetch-now
```

## Out of scope (by design)

No scheduler/`interval_minutes` enforcement (M5), no agent config association, no dashboard or
`/health/sources`, no public OpenAPI document (M8), no admin approval flow, no LLM/engine call
anywhere on these paths.

## Related documentation

- Design source of truth: [`../../plans/01-architecture.md`](../../plans/01-architecture.md) §6
  (service layer), §9 (API), §10 (data), §13.1–§13.7 (model, schema, checklist).
- Task contract: [`../../plans/implementation/M2T5-sources-api-endpoints-crud.md`](../../plans/implementation/M2T5-sources-api-endpoints-crud.md).
- SPA screens + unlock endpoint (amendment R2): [`../../plans/implementation/M2T6-sources-spa-screens.md`](../../plans/implementation/M2T6-sources-spa-screens.md).
- Session-key envelope + account unlock/kdf pair (M5T1, SQ-M5-1): [`../../plans/implementation/M5T1-session-key-envelope-transport.md`](../../plans/implementation/M5T1-session-key-envelope-transport.md).
- Milestone: [`../../plans/implementation/M2-connector-framework-and-imap-source-plugin.md`](../../plans/implementation/M2-connector-framework-and-imap-source-plugin.md).
