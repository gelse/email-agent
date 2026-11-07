## Purpose

This file is the operating manual for agents working in this repository: layout, rules, workflow, and hygiene.

## Project layout

| Path | What lives there | Tracked or git-ignored |
|---|---|---|
| `docs/` | Shared, versioned documentation: architecture, API reference, operational notes. | Tracked |
| `plans/` | Implementation plans and task files; local working artifacts. | Git-ignored |
| `questions/` | Open questions for a human; shared, versioned record. | Tracked |
| `reports/` | AI-generated reports that fit nowhere else: finalize reports, audits, verification evidence. | Git-ignored |
| `src/` | Application source: the API and the Blazor WebAssembly SPA. | Tracked |
| `tests/` | Unit and integration tests; not yet present. | Tracked |
| `docker/` | Container build and compose files. | Tracked |
| `scripts/` | Operational scripts, including build and smoke checks. | Tracked |

`plans/` and `reports/` being git-ignored is intentional; `docs/` and `questions/` are the shared, versioned record.

## Hard constraints

| Rule | Constraint |
|---|---|
| Portable | Target Linux; use only cross-platform APIs; never hard-code path separators. |
| Secrets | Never commit credentials, tokens, keys, or connection strings; use environment variables or the container secret mechanism. |
| Tests | Tests must not call external production services; use recorded fixtures or a local mock. |
| Contracts | Do not change a published interface without updating every implementation, the plans, and the documentation together. |
| Encryption | User data stays encrypted with user-owned keys; the service must not be able to decrypt it. |
| Concurrency | Do not overwrite another agent's in-flight work; review rather than rewrite. |

## Adding a feature — the milestone flow

1. Write the milestone file `plans/M<milestone-num>-<description>.md` with goal, scope, exit criteria, task breakdown, and open questions.
2. Have a **different** agent review and update it; repeat until stable.
3. Write one task file per task: `plans/M<milestone-num>T<task-num>-<description>.md`.
4. Have a **different** agent review and update the task files; repeat until stable.
5. Implement the tasks one after another, in dependency order.
6. After each task, update that task file and the milestone file if the plan changed.
7. When the milestone is complete, write `reports/M<milestone-num>-finalize-report.md`.

## Plan and task file format

| Kind | Required headings |
|---|---|
| Milestone | `# M<n> — <title>`, `## Goal`, `## Scope` (`### In scope` / `### Out of scope`), `## Architecture sections advanced`, `## Dependencies`, `## Exit criteria`, `## Task breakdown`, `## Dependency DAG`, `## Verification strategy`, `## Tests at a glance`, `## Risks and open sub-questions` |
| Task | `# M<n>T<m> — <title>`, `## ID and slug`, `## Goal`, `## Scope` (`### In` / `### Out`), `## Files affected`, `## Depends on`, `## Steps`, `## Acceptance criteria`, `## Tests`, `## Risks`, `## Cross-references`, `## Definition of done` |

## Questions for a human

Record a question in `questions/`; keep the human's answer verbatim; close a question by recording the decision and the place it is implemented. `questions/` holds (a) open items and (b) — for questions actually put to a human — the verbatim answer, the adopted decision, and its `Implemented in:`/`Scheduled in:` citations; planning-time sub-questions decided during planning are recorded in the plan corpus (`plans/01-architecture.md`, milestone summaries, task files) and are not duplicated into `questions/`.

## Reports

Milestone finalize reports, audits, and verification evidence belong in `reports/`; user-facing and operational documentation belongs in `docs/`.

## Git hygiene

- Commit only when asked.
- One logical change per commit.
- Never commit secrets.
- Keep `plans/` and `reports/` untracked.
- Do not rewrite published history without being asked.

## Pointers

- Design source of truth: `plans/01-architecture.md`.
- Milestone order: `plans/03-roadmap.md`.
