# Documentation

This directory holds the project's documentation: architecture overview, API reference, and operational notes.

Design detail lives in `plans/`.

| Document | Contents |
|---|---|
| [`api/sources-endpoints.md`](api/sources-endpoints.md) | User sources CRUD, test-connection, fetch-now, the M5T1 `X-Crypto-Session` unlock flow (`GET /v1/account/kdf` + `POST /v1/account/unlock`), 401-vs-403 problem types, rate limits, pagination. |
| [`operations/plugin-signing-key-rotation.md`](operations/plugin-signing-key-rotation.md) | Runbook for rotating `PLUGIN__SigningKey`: re-sign drop-ins, one-window cutover, verification, rollback. |

Agents add or update documentation here when a change alters how the system is used, deployed, or configured.
