# Sources API (`/v1/user/sources`)

CRUD, connection test, and fetch-now endpoints for a user's mail sources. Introduced in
[M2T5](../../plans/implementation/M2T5-sources-api-endpoints-crud.md); built on the M2T1/M2T3
connector framework (`IMessageSource`, `IPluginRegistry`) and the `SourceService` layer and
`processed_messages` dedupe table introduced by this task. The passphrase → DEK unlock endpoint
(`POST /v1/user/account/unwrap-dek`) and the SPA-side `X-User-Dek` plumbing were added by
[M2T6](../../plans/implementation/M2T6-sources-spa-screens.md) under amendment R2.

All `/v1/user/sources*` routes require a bearer token with the `User` role (`RequireRole("User")`)
and act on the **caller's own sources only** — every query filters by `user_id` taken from the
token, never from the request. The companion `/v1/user/account/*` routes (`kdf`, `unwrap-dek`)
require an authenticated, non-disabled account and likewise resolve the caller's own row from the
token: no role claim is demanded there, and nothing in that namespace reads or writes another
account. Admin routes live under `/v1/admin/*` (see `plans/01-architecture.md` §9).

## Endpoints

| Method | Route | Success | Notes |
|---|---|---|---|
| `GET` | `/v1/user/account/kdf` | `200 {kdf_salt, kdf_params}` | Caller's KDF material (Argon2id over `users.kdf_salt` + `kdf_params`) for tooling that inspects or regenerates KDF parameters. **Unused by the SPA since amendment R2** — the browser no longer derives the DEK itself; see `unwrap-dek` below. `409 /errors/passphrase-not-set` when no passphrase is set yet. Read-only, not audited. |
| `POST` | `/v1/user/account/unwrap-dek` | `200 {dek}` | **Amendment R2 (M2T6).** Body `{"passphrase": "…"}` (whole body capped at 4 KiB); the server derives the KEK with Argon2id and unwraps `users.dek_wrapped`, answering with the raw 32-byte DEK as base64 under `Cache-Control: no-store` — the only way the M2 SPA obtains a DEK. Ordered failures: `401 /errors/unauthorized` when the token's account no longer exists, then `403` disabled account, then `409 /errors/passphrase-not-set` **before the body is read**, then `400 /errors/validation` with `errors[].name = "passphrase"` for a missing, malformed, empty, or over-size body, then `429` + `Retry-After: 1` `/errors/rate-limited` while another unwrap for the same user is in flight, then `401 /errors/credential-mismatch` for a wrong passphrase (audited as `dek_unwrap_failed`; a successful unwrap is not audited). |
| `GET` | `/v1/user/sources` | `200 {items, next_cursor?}` | Keyset cursor pagination; filters `type_id`, `status`. Never 204 — an empty caller has `"items": []`. |
| `POST` | `/v1/user/sources` | `201` + `Location: /v1/user/sources/{id}` | Schema-gates the config against the plugin's `ConfigSchema`; `400 /errors/plugin-not-registered` for an unknown `type_id`. |
| `GET` | `/v1/user/sources/{id}` | `200` | `404 /errors/source-not-found` for other users' ids — **never 403** (no existence oracle). |
| `PATCH` | `/v1/user/sources/{id}` | `200` | Partial update; `interval_minutes` must stay `1..1440`. |
| `DELETE` | `/v1/user/sources/{id}` | `204` | `409 /errors/conflict` while the source still owns `processed_messages` rows (FK `ON DELETE RESTRICT`, Q-O15; relaxation is M6aT7). No DEK header — nothing is decrypted. |
| `POST` | `/v1/user/sources/test-connection` | `200 {ok, error?}` | **Collection-level**, body `{type_id, config}` — tests a config before it is saved, so no source id exists yet. Always `200` for expected failures; see below. |
| `POST` | `/v1/user/sources/{id}/fetch-now` | `200 {processed, new_processed_messages, checkpoint}` | Manual exerciser (no scheduler in M2): fetches new messages, persists dedupe-keyed rows, advances the checkpoint, audits. |

### Authentication and the `X-User-Dek` header (M2-only transport)

Source configs are stored encrypted under a **user-owned data encryption key (DEK)**: since
amendment R2 (M2T6) the browser obtains the DEK by POSTing the passphrase to
`POST /v1/user/account/unwrap-dek`, which unwraps `users.dek_wrapped` server-side and returns
`200 { "dek": "<base64 32 bytes>" }`; the SPA keeps it in memory only (`TokenStore.DekStore`,
lost on refresh → re-prompted) and the passphrase is discarded with the request. Endpoints that
**read or write the encrypted config blob** require the header `X-User-Dek: <base64 of the raw
32-byte DEK>` (the M2 SPA stamps it on `/v1/user/sources*` requests only — login, refresh,
`unwrap-dek` and admin calls never carry a key):

| Endpoint | `X-User-Dek` |
|---|---|
| `GET /v1/user/sources` (list) | required |
| `POST /v1/user/sources` (create) | required |
| `GET /v1/user/sources/{id}` | required |
| `PATCH /v1/user/sources/{id}` | required |
| `POST /v1/user/sources/{id}/fetch-now` | required |
| `POST /v1/user/sources/test-connection` | not required (nothing stored or read) |
| `DELETE /v1/user/sources/{id}` | not required (nothing decrypted) |
| `GET /v1/user/account/kdf` | not required |
| `POST /v1/user/account/unwrap-dek` | not required (it *returns* the DEK) |

Missing, non-base64, or wrong-length values are rejected with `400` and problem detail type
`/errors/validation`, `errors[].name = "x-user-dek"`. A well-formed but **wrong** key (rotated, or
belonging to another account) fails the GCM tag on unwrap → `400 /errors/dek-invalid`.

> **M2-only:** both the header and the `unwrap-dek` round-trip are milestone-M2 transport
> conveniences (SQ-E9 / task Risks SQ-M2-1), documented as transitional — M5 replaces them with
> the session-key envelope (the SPA derives the DEK once after login; the header disappears).
> Nothing outside M2 should depend on either.

### Validation and RFC 7807 problem types

Config validation returns `400` with type `/errors/config-validation` and an `errors[]` array of
`{name, reason}` where `name` is the **wire field name from the plugin JSON schema** (e.g. `port`)
and `reason` the human-readable constraint (e.g. `must be between 1 and 65535`).

| Problem `type` | Status | Cause |
|---|---|---|
| `/errors/unauthorized` | 401 | Anonymous or dead-account token. |
| `/errors/forbidden` | 403 | Wrong role, or a disabled account. |
| `/errors/validation` | 400 | Request shape, `X-User-Dek`, body JSON, cursor, `limit`, or `status` (`errors[].name` = offending member: `config`, `interval_minutes`, `type_id`, `x-user-dek`, `cursor`, `limit`, `status`, `passphrase`, `body`). |
| `/errors/config-validation` | 400 | Config fails the plugin's `ConfigSchema` — raised **before any socket is opened** (e.g. `port: 99999`). |
| `/errors/plugin-not-registered` | 400 | `type_id` not offered by any registered plugin. |
| `/errors/dek-invalid` | 400 | `X-User-Dek` does not unwrap this source's config blob. |
| `/errors/credential-mismatch` | 401 | `POST /v1/user/account/unwrap-dek` with a wrong passphrase — detail "The passphrase is not valid."; audited as `dek_unwrap_failed`. Distinct from `/errors/unauthorized`, which is a token problem. |
| `/errors/source-not-found` | 404 | Missing id **or another user's id** (indistinguishable by design). |
| `/errors/conflict` | 409 | `DELETE` on a source that still has `processed_messages` rows. |
| `/errors/passphrase-not-set` | 409 | `POST /v1/user/account/unwrap-dek` (or `GET /v1/user/account/kdf`) before the first-login passphrase flow; raised before the unwrap body is read. |
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
POST /v1/user/sources                      // X-User-Dek required
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

`POST /v1/user/sources/{id}/fetch-now` (DEK required) validates the stored config again, iterates
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

> **Known limitation (M2):** the 4 KiB body cap applies to *every* unwrap, including passphrases
> set before M2T6, when `set-passphrase` had no 512-character ceiling. A stored passphrase whose
> JSON-escaped form exceeds the cap can therefore never be unwrapped in M2 — `set-passphrase`
> answers `409 /errors/passphrase-already-set`, and re-wrap is M6a. Recorded rather than
> worked around: no M2 endpoint may rewrite `users.dek_wrapped` without a valid KEK. Accounts set
> through the M2 form are unaffected (512 characters escape to well under 4 KiB).

### Example

```bash
# 1. Unlock (amendment R2): exchange the passphrase for the DEK — the SPA's only source of it.
DEK=$(curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"passphrase":"correct horse battery staple"}' \
  https://host/v1/user/account/unwrap-dek | jq -r '.dek')
# Wrong passphrase -> 401 /errors/credential-mismatch; no passphrase set yet -> 409 /errors/passphrase-not-set;
# missing/empty/oversized body -> 400 /errors/validation (errors[].name = "passphrase";
# the body is capped at 4 KiB, and the shared passphrase validator caps the value at 512 characters).

# 2. Optional: inspect the KDF parameters (the SPA no longer derives a DEK in the browser).
curl -sS -H "Authorization: Bearer $TOKEN" https://host/v1/user/account/kdf

# 3. List a page of 2 (next_cursor is URL-safe and echoed verbatim; absent = last page).
curl -sS -H "Authorization: Bearer $TOKEN" -H "X-User-Dek: $DEK" \
  "https://host/v1/user/sources?limit=2"
# …then follow the cursor: "https://host/v1/user/sources?limit=2&cursor=$NEXT_CURSOR"

# 4. Create, then read back — no password field in either response.
curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "X-User-Dek: $DEK" \
  -H "Content-Type: application/json" \
  -d '{"type_id":"imap","display_name":"home","config":{"host":"imap.local","port":143,"use_ssl":false,"username":"a","password":"p","mailbox":"INBOX"}}' \
  https://host/v1/user/sources

# 5. Test a config before saving it (200 even on failure — read `ok`).
curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"type_id":"imap","config":{"host":"imap.local","port":143,"use_ssl":false,"username":"a","password":"p","mailbox":"INBOX"}}' \
  https://host/v1/user/sources/test-connection

# 6. Fetch now (manual exerciser; DEK required).
curl -sS -X POST -H "Authorization: Bearer $TOKEN" -H "X-User-Dek: $DEK" \
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
- Milestone: [`../../plans/implementation/M2-connector-framework-and-imap-source-plugin.md`](../../plans/implementation/M2-connector-framework-and-imap-source-plugin.md).
