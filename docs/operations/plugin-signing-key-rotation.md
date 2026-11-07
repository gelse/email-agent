# Runbook — rotate the plugin signing key (`PLUGIN__SigningKey`)

Re-sign every signed drop-in with a new key, swap `PLUGIN__SigningKey`, restart the api — **in one
maintenance window**. Signatures are HMAC-SHA-256 made with the current key over each plugin's DLL and manifest: a host
holding the new key refuses every drop-in signed with the old one (and vice versa), so both halves must go live together.
Out of sync, the loader fails closed — those sources go down, not compromised.

**Audience:** an operator with repo and host access who knows docker/compose but has not read this
repo's signing internals. All commands run from the repo root.

## Prerequisites

- `docker compose`, `jq`, `openssl` — the same tools `scripts/smoke-m2.sh` already requires.
- `.env` at the repo root; keep the current `PLUGIN__SigningKey` value aside for rollback (never paste
  it into docs, tickets, or commits — `.env` is git-ignored).
- Host-side copies of every signed drop-in: one `<assembly>.dll` plus its `<assembly>.plugin.json`. The
  api container's `/plugins` is **not** a mounted volume — created inside the container, discarded on
  recreate; extract first if you hold no copies: `docker compose cp api:/plugins/<file> ./<file>`

## Steps (one maintenance window)

1. **Back up each companion:** `cp X.plugin.json X.plugin.json.orig`
2. **Generate the new key** — ≥ 32 ASCII bytes; the string's UTF-8 bytes *are* the HMAC key (no hex/base64 decoding anywhere): `NEW_KEY="$(openssl rand -base64 32)"`
3. **Re-sign each drop-in** (DLL + companion side by side). **No signing CLI ships in this repo** — this block is the procedure:

   ```bash
   DLL='Your.Plugin.dll'; MANIFEST='Your.Plugin.plugin.json'
   # canonical manifest = on-disk JSON minus "signature", compacted, member order kept; the signed
   # message = SHA-256(DLL) || SHA-256(canonical manifest) — the loader recomputes both the same way
   printf '%s' "$(jq -c 'del(.signature)' "$MANIFEST")" > /tmp/plugin-canonical.json
   { openssl dgst -sha256 -binary "$DLL"; openssl dgst -sha256 -binary /tmp/plugin-canonical.json; } \
     > /tmp/plugin-signing-input.bin
   SIG="$(openssl dgst -sha256 -hmac "$NEW_KEY" -binary /tmp/plugin-signing-input.bin | openssl base64 -A)"
   jq --arg sig "$SIG" '.signature = $sig' "$MANIFEST" > "$MANIFEST.new" && mv "$MANIFEST.new" "$MANIFEST"
   ```

   Repeat per companion (skip this step if you run no drop-ins). Byte-exactness: for manifests whose string values are
   plain printable ASCII, `jq -c` matches the loader's canonicalization (`PluginSignatureVerifier.SigningInput`); `<`, `>`,
   `&`, `'` or non-ASCII in values may serialize differently under .NET — *Verify* catches that as `signature_mismatch`.
4. **Swap the key** in `.env`: set `PLUGIN__SigningKey=` to the new value.
5. **Recreate from `.env`:** `docker compose down && docker compose up -d` — `restart` never re-reads
   `.env`, while `up` recreates the `PLUGIN__SigningKey` secret from it. The `down` also discards
   container-local `/plugins`, hence step 6.
6. **Restore the drop folder and start a fresh load cycle** (skip if you run no drop-ins):

   ```bash
   docker compose exec -T -u root api sh -c 'mkdir -p /plugins && chown emailagent:emailagent /plugins'
   docker compose cp Your.Plugin.dll api:/plugins/Your.Plugin.dll
   docker compose cp Your.Plugin.plugin.json api:/plugins/Your.Plugin.plugin.json
   docker compose exec -T -u root api chown emailagent:emailagent /plugins/Your.Plugin.dll /plugins/Your.Plugin.plugin.json
   docker compose restart api   # startup scans the drop folder once; restart = new cycle
   ```

7. **Verify** (next section), then delete the `.orig` backups.

## Verify after rotation

```bash
docker compose ps api                 # STATUS must reach healthy (healthcheck = /health/ready)
docker compose logs --no-color api | grep 'Loaded plugin'       # one line per plugin per load cycle
docker compose logs --no-color api | grep 'signature_mismatch'  # must print nothing
```

`down`/`up` cleared old container logs, so the greps cover exactly this window; the database keeps its history — window the audit query yourself:

```bash
docker compose exec -T postgres sh -c \
  'PGPASSWORD="$(cat /run/secrets/postgres_password)" exec psql -h 127.0.0.1 -U emailagent -d emailagent -tA' <<'SQL'
SELECT created_at, action, metadata_json->>'plugin_id', metadata_json->>'reason' FROM audit_log
  WHERE created_at > now() - interval '15 minutes' AND action IN ('plugin_loaded','plugin_rejected');
SQL
```

Expected: a `plugin_loaded` row for every plugin per load cycle — with step 6 the window has two cycles,
so the built-in DEFAULT plugins appear twice; **zero** `plugin_rejected` rows with `reason = 'signature_mismatch'`.
A rejection row names the file in `metadata_json->>'assembly_file'` — old key still in the container → redo step 5; otherwise
re-run step 3 for that file, then step 6's restart. A refusal also unregisters the claimed `plugin_id` for that cycle — users
then see `/errors/plugin-not-registered` ([source endpoints](../api/sources-endpoints.md)). A key under 32 bytes fails startup
(`ValidateOnStart` — the api container exits; the compose file sets no restart policy) with
`Plugin:SigningKey (env PLUGIN__SigningKey) must be present and at least 32 UTF-8 bytes (§16 / SQ-3 / Q-O5).` — fix `.env`, redo step 5.

## Rollback

Restore the old key in `.env`, copy every `X.plugin.json.orig` back over its companion, then repeat
steps 5–6 (recreate, re-copy the *original* files, restart, verify). That restores the pre-window state:
old key pairs with old signatures, drop-ins load on the next cycle, their source ids resolve.
`plugin_approvals` is never touched by a rotation — nothing to restore there — and `plugin_signatures`
re-syncs on the next successful load: it records what loaded, it is not an input to verification. Audit
rows from the window stay: `audit_log` is an append-only HMAC chain — never delete from it.

## How the mechanism works

- **Key:** section `Plugin`, property `SigningKey`, env `PLUGIN__SigningKey` — a plain string of ≥ 32
  UTF-8 bytes, enforced at host start (`ValidateOnStart` in `src/EmailAgent.Api/Program.cs`; in compose
  a Docker secret from `.env`, exported by `docker/entrypoint.sh`). Not `AUTH__SigningKey` (JWTs).
- **Signature:** `base64(HMAC-SHA-256(key, SHA-256(dll) ‖ SHA-256(canonical manifest)))`; the canonical
  manifest is the companion JSON minus its `signature` member, re-serialized — member order is part of
  the digest. Checked in fixed time on every load cycle in
  `src/EmailAgent.Infrastructure/Plugins/PluginSignatureVerifier.cs` (+ `PluginLoader.cs`); a mismatch skips the file, audits `plugin_rejected`
  (`reason = 'signature_mismatch'`), and refusal-wins unregisters the claimed `plugin_id`.
- **Not this key:** the DEFAULT plugins load from build output against `DefaultTrustAnchor` —
  build-time SHA-256 entries in `src/EmailAgent.Infrastructure/Plugins/DefaultTrustAnchor.cs`; rotation
  does not affect them (a disk companion beside a DEFAULT DLL is still HMAC-checked, anchor or not).

## Re-signing vs. approvals, per plugin version

Re-signing is per file pair: **every version you still run** gets a new signature, not just the newest.
Approvals are key-independent — `plugin_approvals` is keyed `(plugin_id, version)`; rotation and
re-signing never modify it, and anchored DEFAULTs need no row. A new, non-anchored version needs its own
row too; no API endpoint writes these (`approved_by_user_id` FK → existing `users.id`):
`INSERT INTO plugin_approvals (plugin_id, version, approved_by_user_id, approved_at) VALUES ('<plugin_id>', '<version>', '<users-uuid>', now());`

## Known gap — no signing CLI yet (M2T3 follow-up) (decided Q-D11, 2026-11-07)

M2T3 deferred both a signing tool and this procedure — the manual `openssl` block above is the shipped
answer until then. From [`plans/implementation/M2T3-plugin-loader-and-signing.md`](../../plans/implementation/M2T3-plugin-loader-and-signing.md),
section *Risks*:

> **Plugin signing key rotation** — Q-O5 supports "admin approval of new versions" but does not name
> a rotation procedure. If `PLUGIN__SigningKey` is rotated, every plugin's `plugin.json` signature
> becomes invalid. **Out of scope for M2**; the operator must re-sign all plugins before rotating.
> Documented as a follow-up.
