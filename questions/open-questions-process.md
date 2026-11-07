# process questions

| Status | Open questions: none — two decisions recorded 2026-11-07 (Q-P1, Q-P2, below) |
|---|---|
| Scope  | Milestone sequencing, PR/merge governance, and cross-category process conventions (including the `questions/` convention itself). |

New questions for this category are recorded here per `AGENTS.md`; for questions actually put to a human this file holds the verbatim answer, the adopted decision, and its `Implemented in:`/`Scheduled in:` citations. Planning-time sub-questions decided during planning stay in the plan corpus (`plans/01-architecture.md`, milestone summaries, task files) and are not duplicated here.

## Q-P1 — Next milestone after M3

| Field | Value |
|---|---|
| Raised | Roadmap sequencing (next milestone after M3); put to the human 2026-11-07 |
| Category | Milestone sequencing |
| Resolved | 2026-11-07, open-decision batch — decision: `M4 (Recommended)` |

**Human's answer (verbatim):** `M4 (Recommended)`

**Decision.** M4 (action dispatcher + IMAP reply/forward action) is the next milestone after M3; it starts only after the human merges PR #1 (the M3 dependency).

**Implemented in:** `plans/03-roadmap.md` — the "Next milestone ready for detailed planning" section and the gating line (2026-11-07).
**Scheduled in:** M4 planning — gated on the human's merge of PR #1.

## Q-P2 — Merge of the M3 PR (#1)

| Field | Value |
|---|---|
| Raised | PR governance for PR #1 (`feature/m3-openai-engine` → `testing`); put to the human 2026-11-07 |
| Category | PR/merge governance |
| Resolved | 2026-11-07, open-decision batch — decision: `Wait for my review` |

**Human's answer (verbatim):** `Wait for my review`

**Decision.** PR #1 (base `testing`, head `feature/m3-openai-engine` @ `1d80e5d`) stays open and unmerged pending the human's review; no automation or merge was performed, and the M3 branch is untouched by this batch.

**Implemented in:** — (external disposition; PR #1 verified open and mergeable at recording time).
**Scheduled in:** the human's review and merge of PR #1 → then M4 per Q-P1.
