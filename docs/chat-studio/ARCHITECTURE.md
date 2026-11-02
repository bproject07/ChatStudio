# Chat Studio — Architecture & Long-Term Memory

[Chat Studio](README.md) · [Operations](OPERATIONS.md) · [Home](../../README.md)

Chat Studio (`src/SearchQuality.Chat`, port `5199`) is a web and Telegram conversational runtime that uses the shared PostgreSQL hybrid retrieval engine as per-user long-term memory. The Personal Fitness Coach (`coach`) is implemented as a specialist agent on top of the general chat, handoff, deliverable, and structured activity mechanisms.

```powershell
dotnet run --project .\src\SearchQuality.Chat
```

## Core Turn Flow

```text
User message
  → Load active agent for the session from PostgreSQL
  → Search archive chunks scoped strictly to this user_id
  → Assemble prompt input: active prompt version + current date (Europe/Sofia)
      + current deliverables summary cards + retrieved archive context
      + session summary seed + conversation history + user message
  → Model decides whether to reply directly or invoke tools
  → Tool results are returned to the model in an iterative loop
  → Final response, provenance (agent, model, prompt version), steps, and tokens are persisted
```

The primary agent `chat` acts as the orchestrator. Fitness and training questions are delegated via a real `handoff_to` tool call to the `coach` specialist. The handoff task describes the requested outcome and known facts without injecting synthetic onboarding questionnaires.

When a user already has a saved training program and asks about today's workout, tomorrow's session, a specific day, or an exercise, the `coach` reads the current deliverable and SQL workout diary first and answers directly. Onboarding questions (goals, experience, sleep, equipment, schedule) are asked only when creating a brand-new program and only for genuinely missing information.

Natural-language understanding belongs to the active prompt and language model. Application code intentionally avoids hardcoded phrase lists, regex intent classifiers, deterministic workout-day parsers, or hidden "corrective" LLM loops. This keeps behavior transparent and adjustable via database-versioned prompts without redeploying binaries.

## Code-Level Guarantees

C# application code enforces deterministic invariants that must never be left to an LLM:

- Strict data isolation by `user_id` across all queries and mutations;
- Real specialist handoff and persistent `sessions.active_agent` state;
- Allowlisted agent keys and per-role tool catalogs;
- Strict JSON schema and ISO date (`YYYY-MM-DD`) validation;
- Atomic SQL transactions for batch diary logging (`log_activities`);
- Duplicate protection for the same `(kind, label, happened_on)` within a day unless an explicit `replaces_id` correction or `allow_duplicate=true` is supplied;
- Immutable document versions, create-only initial save (`save_deliverable`), atomic exact-text edits (`edit_deliverable`), and optimistic concurrency locking on full replacement (`replace_deliverable`);
- At most one successful document mutation per document kind within a single turn;
- Accurate token accounting, persistence, and automatic context compaction;
- Stream protocol integrity and safe recovery on error or client cancellation.

## Models and Providers

- Text generation routes through LiteLLM (`/chat/completions`) or directly through the OpenAI Responses API (`openai` provider).
- Embeddings for archive memory search always route to the configured OpenAI embeddings endpoint (`text-embedding-3-large`).
- Each agent stores its provider, model, and active prompt version in `chat_lab.agents`.
- Available models in the Web UI are fetched live from the provider's `/models` endpoint.

## Database-Versioned Prompts

Baseline prompt policies live in `ChatPromptPolicy.cs`. On an empty database, `chat`, `coach`, and `compactor` are seeded as `v1` via `DefaultPrompts`.

In a running environment, prompts are versioned in `chat_lab.prompt_versions`. Saving an edit creates a new immutable version row and updates `agents.active_prompt_version`. Every assistant message records the agent key, model name, and prompt version that produced it, allowing behavior changes or instant rollbacks without code changes.

Role boundary protocols appended by code define only tool and handoff contracts:

- `chat` discovers specialists (`find_specialists`) and performs real handoffs (`handoff_to`) for fitness tasks;
- `coach` inspects current deliverables (`get_deliverable`) and the SQL workout diary (`get_log`, `get_progress`) before asking questions, and calls `finish_task` when done;
- `compactor` preserves durable user facts while avoiding turning temporary advice or computed diary state into permanent rules.

## PostgreSQL Schema (`chat_lab`)

| Table | Purpose |
|---|---|
| `documents`, `chunks` | Archived conversation summaries and deliverable versions with their searchable chunks (`tsvector` + `pgvector`) |
| `sessions`, `messages` | Conversations, raw turns, agent/model/prompt provenance, tool steps, and token metrics |
| `agents`, `prompt_versions` | Registered agents, model bindings, and immutable prompt version history |
| `user_artifacts`, `artifact_notes` | Pointer to the active deliverable version per `(user_id, kind)` and append-only user notes |
| `activity_log` | Structured workout/activity events, corrections (`replaces_id`), and voided entries |
| `channel_links` | Mapping between external channels (e.g., Telegram chat ID) and internal `user_id` |

Raw conversation turns, active deliverables, and the SQL activity log are the primary sources of truth. Archived summaries are searchable long-term memory; if a conflict arises, current deliverables and SQL diary records take precedence.

## Archive Memory Retrieval

On every turn, `ChatArchiveStore.SearchAsync` executes the selected retrieval strategy (`postgres-hybrid`, `postgres-pgvector`, or `postgres-full-text`) scoped to the active user's archives and returns the top `K` chunks. Retrieved excerpts are labeled as dated historical memory so the model can weigh them appropriately against current documents.

Active deliverables are not mixed into archive chunks automatically; instead, compact metadata cards of available deliverables are injected into the turn context, and the agent fetches the full current document via `get_deliverable` when needed.

## Deliverables, Notes, and Workout Diary Tools

| Domain | Tools | Invariant |
|---|---|---|
| Current deliverable | `get_deliverable` | Returns the full content and version ID of the active document |
| New deliverable | `save_deliverable` | Succeeds only if no document of that `kind` exists yet for the user |
| Local edit | `edit_deliverable` | Applies all exact `old_text` → `new_text` replacements atomically |
| Full replacement | `replace_deliverable` | Requires `current_document_id` matching the latest read version (optimistic lock) |
| User notes | `add_note`, `get_notes` | Append-only notes per user |
| Activity diary | `log_activity`, `log_activities`, `get_log` | Single or batch transactional SQL logging |
| Correction / Void | `replaces_id`, `void_activity` | Previous row is preserved for audit trail |
| Progress summary | `get_progress` | Aggregations computed directly in PostgreSQL |

## Specialist Handoff Protocol

The orchestrator (`chat`) has access to:

- `find_specialists` — lists enabled specialist agents and their activation descriptions;
- `handoff_to(agent_key, task)` — sets `sessions.active_agent` and records a system handoff note;
- shared read and diary tools.

Specialists additionally receive document mutation tools (`save_deliverable`, `edit_deliverable`, `replace_deliverable`) and `finish_task(summary)`, which returns control to `chat`. If a specialist is disabled, missing, or encounters an unhandled exception/timeout, `active_agent` is automatically cleared so the session never gets stuck.

Intermediate tool-calling iterations are buffered on the server; only the final user-facing reply is streamed or emitted to the client.

## Context Token Meter & Compaction

The context meter tracks the `input_tokens` of the most recent model call against `Chat:ContextTokenBudget` (warning at 85%, automatic compaction at 100%). Total cumulative tokens across all tool iterations are recorded for cost auditing.

When compaction triggers, the `compactor` agent summarizes the chronological conversation (excluding internal system notes) into a Markdown archive under `data/chat-history/`, indexes its chunks into `chat_lab`, closes the old session, and seeds the new session with `summary_seed`.

## HTTP API (`/api/chat`)

| Endpoint | Description |
|---|---|
| `GET /profiles` | Configured user profiles and default user ID for UI bootstrap |
| `GET /state?user=` | Active conversation, token meter, runtime settings, and archive list |
| `POST /message`, `/message/stream` | Execute a synchronous or streaming (NDJSON) chat turn |
| `POST /compact`, `/new` | Trigger manual compaction or start a new conversation |
| `POST /handback` | Manually return control from a specialist to the orchestrator |
| `GET /agents`, `/tools` | List registered agents and exact tool schemas per role |
| `POST /agents/{key}/versions`, `/activate` | Create a new immutable prompt version or activate an existing version |
| `GET /history/...`, `/archives/...` | Inspect raw session history, chunks, and Markdown archives |

All state-changing `POST` endpoints require the trusted header `X-Search-Lab: 1` and loopback host verification.

## Telegram Adapter

The Telegram hosted service (`TelegramBotService`) is a thin adapter over `ChatService` sharing the same users, memory, agents, handoffs, and tools as the Web UI:

- `/start` links a Telegram chat to a user profile;
- `/unlink` removes the channel link;
- `/new` starts a fresh session;
- `/back` returns control to the main assistant.

## Phoenix Observability

Optional observability is implemented in `SearchQuality.Chat/Observability` on top of `ActivitySource` and OpenTelemetry/OTLP with OpenInference attributes. Tracing runs inside the application code, which knows the actual outcome of tool executions. LiteLLM remains the route to the models; no separate Phoenix callback is enabled in the proxy.

Every Web or Telegram turn creates a root `chat.turn` span linked to the user, session, and channel. Nested child spans cover agents, model calls, HTTP attempts, tool executions, archive retrieval, embeddings, turn persistence, and automatic compaction. Manual compaction creates its own root trace linked to the same session. Model spans inherit the active agent and prompt version used.

When content capture (`CHAT_TELEMETRY_CAPTURE_CONTENT=true`) is enabled, system prompts, sent context, tool schemas and arguments, received model responses, retrieved chunks, and operation results are recorded. A rejected validation marks `ERROR` on the corresponding tool span even if the agent subsequently recovers and completes the turn. Caught retrieval or compaction errors remain visible on their spans without altering the existing fallback behavior. The persistence span marks success after `AppendTurnAsync` completes.

Token counts are recorded once on the individual LLM and embedding spans. Aggregate chat turn usage is not published as an additional LLM usage attribute, and Phoenix does not alter the existing Chat context token meter. Individual HTTP attempt spans show retry latency and status codes without duplicating token counts. Streaming responses remain streaming and are recorded after assembly; raw embedding vectors are never exported.

The integration is disabled by default. Capturing conversation content requires explicit opt-in, and registered secrets are redacted before export. OTLP uses a dedicated authentication key over HTTPS or loopback HTTP. Export runs in the background with a bounded queue and timeout so an unavailable Phoenix collector never fails a chat turn. Consequently, telemetry spans may be dropped during an outage and do not replace the authoritative PostgreSQL records. There is no automatic historical backfill, automated quality judge, or guaranteed pricing for custom model aliases. Configuration and verification checks are documented in [OPERATIONS.md](OPERATIONS.md) and the [Phoenix deployment guide](../../deploy/phoenix/README.md).

## Deployment

The architecture does not hardcode a hostname, absolute directory, service manager, or active model for a specific environment. The general deployment workflow, backup boundaries, and verification checks are documented in [OPERATIONS.md](OPERATIONS.md). Environment-specific values belong in the protected operator runbook and the configuration of the target environment.
