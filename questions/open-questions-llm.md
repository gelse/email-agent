# llm questions

| Status | Open questions: none — Q-L15 resolved (decision and implementation recorded below) |
|---|---|
| Scope  | Model catalog and endpoint topology, prefilter semantics, tool calling, cost caps, probing, and test fixtures. |

New questions for this category are recorded here per `AGENTS.md`. A question is kept until it is answered; once answered, the decision is recorded in the plan corpus (milestone summaries and `plans/01-architecture.md`) and the entry is removed.

## Q-L15 — the probe's transmitted request lacks `max_tokens: 1` (uncapped probe generation)

| Field | Value |
|---|---|
| Raised | 2026-11-07, M3T2 review (review-code finding 1) |
| Category | Tool calling / probing / cost caps |
| Resolved | 2026-11-07, M3 follow-up fix — decision: option 1 (additive plugin-local `EngineCallOptions.MaxTokens`) |

**Context.** SQ-M3-3's canonical probe body specifies `max_tokens: 1` "to keep the cost bounded". The M3T2 audit row's `request_excerpt` records exactly that canonical body (built by `ProbeRequestBuilder.BuildWireBody`, byte-for-byte per the task contract). The request the engine *actually transmits* is built by M3T1's `OpenAIRequestBuilder.Build` (`src/EmailAgent.Connectors.OpenAI/Http/OpenAIRequestBuilder.cs`), whose `ChatCompletionRequest` has **no `max_tokens` member at all** — so every live probe call is uncapped: the provider generates until its own default stop, on the admin's account, on every POST/PATCH and every window expiry. The audit row therefore documents intent, not the transmitted body (deviation D7).

**Why it is out of M3T2's hands.** The probe must not use a direct HTTP client (acceptance criterion) and must not modify M3T1's engine assembly (scope). Threading a token cap requires an engine-side change: either a `MaxTokens` member on `EngineCallOptions`, an `AgentRequest`/`AgentContext` knob read by `OpenAIRequestBuilder`, or adding `max_tokens` to `ChatCompletionRequest` for all calls — each a cross-milestone contract decision (published `IAgentEngine` surface, `plans/01-architecture.md` §9/§11, M3T6's test-completion call shares the same builder).

**Options.**

1. Add `max_tokens` to `EngineCallOptions` (default `null` = omit) and have `OpenAIRequestBuilder.Build` emit it when set; M3T2's probe passes `1`. Bounds the probe only; additive, no behavior change for existing callers.
2. Add `AgentRequest.MaxTokens` (or an `AgentContext` member) so any caller can bound a completion — wider contract change, touches the Contracts assembly.
3. Always emit `max_tokens` on non-streaming calls from a config default — changes every engine call, needs its own defaults/§16 entry.
4. Accept uncapped probe generation (documented cost exposure) and keep `max_tokens: 1` audit-only.

**Leaning (not a decision):** option 1 — smallest additive change, matches the task's own wording ("the default `EngineCallOptions`" with `max_tokens: 1` as a probe-specific bound), and M3T6 can reuse it.

**Asked of:** the human. Record the answer verbatim here; when decided, note the implemented location in this row and move it into the plan corpus per `AGENTS.md`.

**Carry-forward (review-code confirmation pass):** decide this **before M3T3** exposes the probe through `POST/PATCH /v1/admin/models` — that is when admin-triggered, uncapped probe calls become billable. Today the probe has no production caller, and each call is still bounded by the engine's whole-call deadline.

**Decision (2026-11-07, M3 follow-up, per the plan of record SQ-M3-3 — "transmit the canonical probe body"):** **option 1 adopted.** The plugin-local `EngineCallOptions` gains an additive nullable `MaxTokens` (`int?`, default `null` = the field is omitted), `OpenAIRequestBuilder.Build` emits `"max_tokens": N` only when the option is set, and M3T2's probe passes `1`, so every live probe call now transmits SQ-M3-3's canonical `max_tokens: 1` cost bound instead of running uncapped. `EngineCallOptions.Default` and all existing callers keep the exact pre-fix request shape (no `max_tokens` on the wire unless a call opts in); the published `IAgentEngine` surface and `EmailAgent.Contracts` stay verbatim (option 2 rejected), no config-wide default is imposed on other engine calls (option 3 rejected), and the cost bound is now live on the wire rather than audit-only (option 4 rejected). The audit `request_excerpt` keeps recording the canonical SQ-M3-3 body; the canonical field set it records (model, messages `[user "ping"]`, the ping tool, `tool_choice: "auto"`, `max_tokens: 1`, `stream: false`) is exactly what the engine transmits — the only remaining textual differences are M3T1's documented tool-`parameters` flattening (`{}` for ping) and member serialization order (M3T2 deviation D7), both pinned by M3T1's request-shape tests.

**Implemented in:** M3 follow-up fix on branch `feature/m3-openai-engine`, amended into M3T2's commit (`M3T2: tool-calling capabilities probe`): `src/EmailAgent.Connectors.OpenAI/EngineCallOptions.cs` (the `MaxTokens` member), `src/EmailAgent.Connectors.OpenAI/Http/OpenAIRequestBuilder.cs` (`max_tokens` emitted only when set), `src/EmailAgent.Connectors.OpenAI/OpenAICompatibleAgentEngine.cs` (forwards `options.MaxTokens` into the builder), `src/EmailAgent.Api/Models/ModelProbeService.cs` (`ProbeCallOptions` with `MaxTokens = ProbeRequestBuilder.ProbeMaxTokens`, dispatched through the concrete engine's options-carrying overload). Wire shape pinned by `tests/EmailAgent.UnitTests/Connectors/OpenAI/OpenAIRequestBuilderTests.cs` + `EngineCallOptionsTests.cs` (emit/omit + default unchanged), `tests/EmailAgent.UnitTests/Models/ModelProbeServiceTests.cs` (probe → captured transmitted body) and `tests/EmailAgent.IntegrationTests/Models/ModelProbeServiceTests.cs` (over the wire, via the mock's `LastRequestBody` capture in `tests/EmailAgent.IntegrationTests/Mocks/OpenAIMock/Program.cs`).
