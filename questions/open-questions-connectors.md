# connectors questions

| Status | Open questions: none — two decisions recorded 2026-11-07 (Q-C8, Q-C9, below) |
|---|---|
| Scope  | IMAP/SMTP connectors, OAuth, attachments, plugin DTO/manifest contracts, and egress allow-listing. |

New questions for this category are recorded here per `AGENTS.md`; for questions actually put to a human this file holds the verbatim answer, the adopted decision, and its `Implemented in:`/`Scheduled in:` citations. Planning-time sub-questions decided during planning stay in the plan corpus (`plans/01-architecture.md`, milestone summaries, task files) and are not duplicated here.

## Q-C8 — Plugin egress allow-list posture at M9

| Field | Value |
|---|---|
| Raised | M3 SQ-M3-5 / Q-CE3; put to the human 2026-11-07 |
| Category | egress allow-listing |
| Resolved | 2026-11-07, open-decision batch — decision: `Keep wildcard + optional config tightening (Recommended)` |

**Human's answer (verbatim):** `Keep wildcard + optional config tightening (Recommended)`

**Decision.** Keep `http_allowlist: ["*"]` as the v1 default; optional config tightening is deferred. Decided — no code change; revisit at M9.

**Implemented in:** plan record: `plans/01-architecture.md` §9 note (2026-11-07); the wildcard itself ships with PR #1 on `feature/m3-openai-engine` (`src/EmailAgent.Connectors.OpenAI/plugin.json`).
**Scheduled in:** M9 — revisit wildcard vs configured allow-list.

## Q-C9 — Stale `plugin.json` field names (`id` / `required_capabilities`)

| Field | Value |
|---|---|
| Raised | M2T1 review S6 — stale `plugin.json` field-name prose; put to the human 2026-11-07 |
| Category | plugin DTO/manifest contracts |
| Resolved | 2026-11-07, open-decision batch — decision: `Fix prose + reject old names (Recommended)` |

**Human's answer (verbatim):** `Fix prose + reject old names (Recommended)`

**Decision.** Fix the prose to the shipped SQ-1 shape AND make parsing fail loudly with an exception naming the legacy field; shipped manifests are unchanged.

**Implemented in:** `src/EmailAgent.Contracts/Plugins/PluginManifest.cs` (legacy-field guard) with tests in `tests/EmailAgent.UnitTests/Contracts/PluginManifestTests.cs`, plus prose fixes at `plans/01-architecture.md` §9 and `plans/implementation/M8-telegram-action-handler.md` (2026-11-07).
**Scheduled in:** — .
