# deployment questions

| Status | Open questions: none — two decisions recorded 2026-11-07 (Q-D11, Q-D12, below) |
|---|---|
| Scope  | Container orchestration, process model, plugin isolation, Windows portability, plugin signing key, and build/CI foundation conventions. |

New questions for this category are recorded here per `AGENTS.md`; for questions actually put to a human this file holds the verbatim answer, the adopted decision, and its `Implemented in:`/`Scheduled in:` citations. Planning-time sub-questions decided during planning stay in the plan corpus (`plans/01-architecture.md`, milestone summaries, task files) and are not duplicated here.

## Q-D11 — Plugin signing-key rotation procedure

| Field | Value |
|---|---|
| Raised | M2T3 Risks — `PLUGIN__SigningKey` rotation follow-up; put to the human 2026-11-07 |
| Category | plugin signing key |
| Resolved | 2026-11-07, open-decision batch — decision: `Document a runbook now (Recommended)` |

**Human's answer (verbatim):** `Document a runbook now (Recommended)`

**Decision.** Document rotation now: re-sign all plugins and rotate `PLUGIN__SigningKey` in one maintenance window; no signing CLI ships yet, so the runbook carries manual `openssl`/HMAC steps.

**Implemented in:** `docs/operations/plugin-signing-key-rotation.md` (new runbook; linked from `docs/README.md`), 2026-11-07.
**Scheduled in:** signing tooling — the M2T3 follow-up (`plans/implementation/M2T3-plugin-loader-and-signing.md`, Risks) — reason: no CLI ships yet; the runbook's manual steps stand until one does.

## Q-D12 — `DefaultTrustAnchor` SHA maintenance

| Field | Value |
|---|---|
| Raised | M2T3 Risks — build-time SHA-256 generation follow-up; put to the human 2026-11-07 |
| Category | plugin isolation — trust-anchor SHA maintenance |
| Resolved | 2026-11-07, open-decision batch — decision: `Keep manual + test guard (Recommended)` |

**Human's answer (verbatim):** `Keep manual + test guard (Recommended)`

**Decision.** Keep the SHA entries in `src/EmailAgent.Infrastructure/Plugins/DefaultTrustAnchor.cs` maintained by hand; the integration tests in `tests/EmailAgent.IntegrationTests/Plugins/DefaultTrustAnchorTests.cs` are the drift guard; no automation. Decided — no code change; revisit is triggered when that guard fails (e.g. at the next connector/plugin release).

**Implemented in:** plan record: `plans/implementation/M2T3-plugin-loader-and-signing.md` (Risks, 2026-11-07).
**Scheduled in:** — (nothing scheduled; the test guard is the revisit trigger).
