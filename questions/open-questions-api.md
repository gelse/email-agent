# api questions

| Status | Open questions: none — two decisions recorded 2026-11-07 (Q-API52, Q-API53, below) |
|---|---|
| Scope  | The single HTTP API + SPA contract: versioning, OpenAPI, CORS, tokens, errors, pagination, rate limits, the SSE live log, and the SPA surface. |

New questions for this category are recorded here per `AGENTS.md`; for questions actually put to a human this file holds the verbatim answer, the adopted decision, and its `Implemented in:`/`Scheduled in:` citations. Planning-time sub-questions decided during planning stay in the plan corpus (`plans/01-architecture.md`, milestone summaries, task files) and are not duplicated here.

## Q-API52 — `GET /v1/user/models` 403 tests and keyset cursor (M3T7 known gaps)

| Field | Value |
|---|---|
| Raised | M3T7 known gaps; put to the human 2026-11-07 |
| Category | errors + pagination (403 tests, keyset cursor) |
| Resolved | 2026-11-07, open-decision batch — decision: `Defer both to M5/M6a (Recommended)` |

**Human's answer (verbatim):** `Defer both to M5/M6a (Recommended)`

**Decision.** Defer both known gaps; no code change now — 403-for-disabled-accounts moves to M5 (user-namespace routes), the keyset cursor to M6a (only if the catalog grows).

**Implemented in:** decision recorded — `reports/M3-finalize-report.md` (Follow-ups) and the M3T7 known-gaps annotation in `plans/implementation/M3T7-m3-end-to-end-integration-and-verification.md` (2026-11-07).
**Scheduled in:** M5 (403 handling) and M6a (cursor) — reason: both are outside M3T7's named test list.

## Q-API53 — Six deferred M3T5 SPA suggestions

| Field | Value |
|---|---|
| Raised | M3T5 review-code (six SUGGESTIONs); put to the human 2026-11-07 |
| Category | the SPA surface |
| Resolved | 2026-11-07, open-decision batch — decision: `Fold usability ones into M7b (Recommended)` |

**Human's answer (verbatim):** `Fold usability ones into M7b (Recommended)`

**Decision.** The usability suggestions (b) `@key` dialogs to their target (dialog reset) and (f) the price-field "leave blank to keep the stored price" hint fold into M7b; (a), (c), (d) and (e) remain deferred with their recorded rationale.

**Implemented in:** `plans/implementation/M7-admin-api-and-spa-endpoints-full.md` — M7b scope (two bullets), and the disposition note at `plans/implementation/M3T5-admin-models-spa-screens.md` (2026-11-07).
**Scheduled in:** M7b (the two usability items); the other four stay deferred per their rationale.
