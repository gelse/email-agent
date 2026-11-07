# llm questions

| Status | Open questions: none — two decisions recorded 2026-11-07 (Q-L16, Q-L17, below) |
|---|---|
| Scope  | Model catalog and endpoint topology, prefilter semantics, tool calling, cost caps, probing, and test fixtures. |

New questions for this category are recorded here per `AGENTS.md`; for questions actually put to a human this file holds the verbatim answer, the adopted decision, and its `Implemented in:`/`Scheduled in:` citations. Planning-time sub-questions decided during planning stay in the plan corpus (`plans/01-architecture.md`, milestone summaries, task files) and are not duplicated here.

## Q-L16 — Cost and price precision (M3T4 D5)

| Field | Value |
|---|---|
| Raised | M3T4 deviation D5; put to the human 2026-11-07 |
| Category | Model catalog — price/cost precision (cost caps) |
| Resolved | 2026-11-07, open-decision batch — decision: `Precision on prices + cost_usd (Recommended)` |

**Human's answer (verbatim):** `Precision on prices + cost_usd (Recommended)`

**Decision.** Add explicit `HasPrecision(10, 6)` to `model_catalog.price_per_1k_input_usd`, `model_catalog.price_per_1k_output_usd` **and** `processed_messages.cost_usd`, as ONE additive migration, applied before M5 writes costs; leaving the columns as unconstrained `numeric` was rejected.

**Implemented in:** decision recorded — `plans/01-architecture.md` §6 and `plans/implementation/M3T4-token-accounting-and-cost-capture.md` (D5 closed as decided, 2026-11-07); no migration applied in this batch.
**Scheduled in:** `feature/m3-openai-engine`, immediately after PR #1 merges (M3 follow-up list) — reason: the additive migration is deliberately kept out of the open PR while the branch is under human review, and it must land before M5 writes costs.

## Q-L17 — Audit/error redaction posture at M9

| Field | Value |
|---|---|
| Raised | M3 `SanitizeDetail` redaction follow-up (M9 revisit item); put to the human 2026-11-07 |
| Category | Probing — test-completion error redaction |
| Resolved | 2026-11-07, open-decision batch — decision: `Keep regexes (Recommended)` |

**Human's answer (verbatim):** `Keep regexes (Recommended)`

**Decision.** Keep the regex-based redaction shipped with M3 (the `sk-…` / `Bearer …` `SanitizeDetail` regexes); generalization to exact configured-key redaction is deferred. Decided — no code change.

**Implemented in:** — (no code change); plan record: `plans/01-architecture.md` §13.3 note (2026-11-07). The regexes themselves ship on `feature/m3-openai-engine` with PR #1 (`src/EmailAgent.Api/Models/TestCompletionService.cs`).
**Scheduled in:** M9 (Observability and audit hardening) — revisit/generalize the redaction.
