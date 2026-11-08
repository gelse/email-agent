# operations questions

| Status | Open questions: none — six decisions recorded 2026-11-07 (Q-O23–Q-O28, below) |
|---|---|
| Scope  | Audit log (retention, integrity, HMAC, action names), multi-tenancy fairness, scheduling and retries, rate limits, backups, first-admin bootstrap, prod-compose ownership, and the Telegram reactive egress posture. |

New questions for this category are recorded here per `AGENTS.md`; for questions actually put to a human this file holds the verbatim answer, the adopted decision, and its `Implemented in:`/`Scheduled in:` citations. Planning-time sub-questions decided during planning stay in the plan corpus (`plans/01-architecture.md`, milestone summaries, task files) and are not duplicated here.

## Q-O23 — `questions/` corpus convention (corpus debt)

| Field | Value |
|---|---|
| Raised | Corpus-debt review of the `questions/` convention itself; put to the human 2026-11-07 |
| Category | cross-category process conventions (the `questions/` convention itself) |
| Resolved | 2026-11-07, open-decision batch — decision: `Amend the convention (Recommended)` |

**Human's answer (verbatim):** `Amend the convention (Recommended)`

**Decision.** Adopt the amended convention: `questions/` holds (a) open items and (b) — for questions actually put to a human — the verbatim answer, the adopted decision, and `Implemented in:`/`Scheduled in:` citations. Planning-time sub-questions that were decided during planning live in the plan corpus (`plans/01-architecture.md`, milestone summaries, task files) and are **not** duplicated into `questions/`.

**Implemented in:** `AGENTS.md` ("Questions for a human"), the header/status lines of every `questions/open-questions-*.md`, `questions/02-open-questions.md`, and the re-pointing of stale `questions/`-persistence claims (`Q-XX` entries presented as living in `questions/`) across the milestone summaries in `plans/` (2026-11-07).
**Scheduled in:** — .

## Q-O24 — Smoke scratch-file guard (M3 verify finding S3)

| Field | Value |
|---|---|
| Raised | M3 verify finding S3; put to the human 2026-11-07 |
| Category | operational scripts — smoke scratch-file guard |
| Resolved | 2026-11-07, open-decision batch — decision: `Add writability pre-check (Recommended)` |

**Human's answer (verbatim):** `Add writability pre-check (Recommended)`

**Decision.** A writability pre-check at the start of each smoke script fails fast with an actionable message (root-owned leftovers + fix commands) instead of a misleading later error.

**Implemented in:** `scripts/smoke.sh` and `scripts/smoke-m2.sh` (2026-11-07).
**Scheduled in:** `scripts/smoke-m3.sh` on `feature/m3-openai-engine` (M3 follow-up) — reason: that script exists only on the M3 branch, which must not be touched pending the human's review of PR #1.

## Q-O25 — GitHub branch protection on `main`

| Field | Value |
|---|---|
| Raised | M0 SQ-M0-6 branch-protection rollout item; put to the human 2026-11-07 |
| Category | build/CI governance — branch protection |
| Resolved | 2026-11-07, open-decision batch — decision: `Enable now on main (Recommended)` |

**Human's answer (verbatim):** `Enable now on main (Recommended)`

**Decision.** Protect `main` only; do not protect `testing`.

**Implemented in:** GitHub branch protection applied via the API on 2026-11-07 and confirmed by read-back: `required_status_checks` strict = true with contexts `build`, `test`, `container-build`, `compose-config`, `smoke`; `allow_force_pushes` = false; `allow_deletions` = false; `enforce_admins` = false; no required PR reviews; no restrictions; `testing` confirmed unprotected.
**Scheduled in:** — .

## Q-O26 — `/metrics` access

| Field | Value |
|---|---|
| Raised | `/metrics` unauthenticated posture (Q-API42 follow-up); put to the human 2026-11-07 |
| Category | observability — `/metrics` access posture |
| Resolved | 2026-11-07, open-decision batch — decision: `Accept until M9 (Recommended)` |

**Human's answer (verbatim):** `Accept until M9 (Recommended)`

**Decision.** Accept unauthenticated `/metrics` (network-exposure only) until M9. Decided — no code change.

**Implemented in:** plan record: `plans/01-architecture.md` §15 note (2026-11-07).
**Scheduled in:** M9 (Observability and audit hardening) — revisit metrics authentication.

## Q-O27 — M0 exit-report pending-evidence line

| Field | Value |
|---|---|
| Raised | M0 exit-report EC2 pending-evidence line; put to the human 2026-11-07 |
| Category | report evidence — M0 exit report |
| Resolved | 2026-11-07, open-decision batch — decision: `Backfill (Recommended)` |

**Human's answer (verbatim):** `Backfill (Recommended)`

**Decision.** Replace the pending line with the first green `main` CI run evidence.

**Implemented in:** `reports/m0-exit-report.md` EC2 — run 42060548529 (first success; artifact confirmation) plus the known-gaps line update (2026-11-07).
**Scheduled in:** — .

## Q-O28 — M5T1 stale cross-references

| Field | Value |
|---|---|
| Raised | `plans/implementation/M5T1-session-key-envelope-transport.md` lines ~19 and ~42 cite the M2 in-browser derivation pattern, which never shipped; put to the human 2026-11-07 |
| Category | plan cross-references — M5T1 |
| Resolved | 2026-11-07, open-decision batch — decision: `Fix at M5 planning (Recommended)` |

**Human's answer (verbatim):** `Fix at M5 planning (Recommended)`

**Decision.** Annotate now; re-derive the correct references at M5 planning.

**Implemented in:** "MUST FIX at M5 planning" annotations at both locations in `plans/implementation/M5T1-session-key-envelope-transport.md` (2026-11-07).
**Scheduled in:** M5 planning, before M5T1 implementation — reason: the correct reference can only be fixed while M5's design is being planned.
