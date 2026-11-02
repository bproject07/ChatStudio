# ChatStudio

An ASP.NET Core (.NET 10) multi-agent conversational platform featuring a built-in **Personal Fitness Coach (`coach`)**, long-term hybrid memory (**PostgreSQL `pgvector` + Full-Text Search with Reciprocal Rank Fusion**), strict per-user data isolation, database-versioned prompts, atomic SQL workout logging, versioned user artifacts/documents, OpenTelemetry/Phoenix observability, and a Telegram bot adapter.

## Key Features

- **Orchestrator & Specialist Handoff (`handoff_to` / `finish_task`):** The primary `chat` orchestrator discovers enabled specialists and delegates domain tasks (such as workout planning, exercise modifications, and training queries) to the `coach` specialist with real state transitions in PostgreSQL (`sessions.active_agent`).
- **Long-Term Hybrid Memory:** Conversations are compacted into structured summaries (`compactor` agent) and indexed in PostgreSQL using both `pgvector` cosine similarity and PostgreSQL GIN Full-Text Search, merged via **Reciprocal Rank Fusion (RRF)** and strictly isolated by `user_id`.
- **Database-Versioned Prompts:** Every prompt edit creates an immutable version in `prompt_versions` and updates `agents.active_prompt_version`. Every model turn records the exact agent, model, and prompt version used, enabling instant prompt iteration and rollback from the Web UI without redeploying code.
- **Authoritative Documents & Workout Diary:**
  - **Deliverables (`user_artifacts`):** Immutable document versions with create-only `save_deliverable`, atomic exact-match local edits (`edit_deliverable`), and optimistic-concurrency full replacement (`replace_deliverable`).
  - **Structured Activity Log (`activity_log`):** Transactional single and batch logging (`log_activity`, `log_activities`), duplicate protection, audit-preserving corrections (`replaces_id`, `void_activity`), and SQL-computed progress (`get_progress`).
- **Web UI & Telegram Adapter:** Streaming NDJSON chat UI, raw conversation and chunk history inspector, agent/prompt management UI, context token meter, and an optional Telegram bot adapter sharing the same `ChatService` and user state.
- **OpenInference / Arize Phoenix Observability:** Optional OpenTelemetry tracing (`chat.turn`, LLM calls, HTTP retries, tool executions, archive retrieval, embeddings, and compaction) with secret redaction.

## Project Structure

| Project / Directory | Responsibility |
|---|---|
| `src/SearchQuality.Chat` | ASP.NET Core Web & Telegram runtime, `chat` / `coach` / `compactor` agents, tools, compaction, observability, and static Web UI |
| `src/SearchQuality` | Shared retrieval core: PostgreSQL Hybrid RRF, `pgvector`, Full-Text Search, document chunking, embeddings, and resilient OpenAI/LiteLLM HTTP clients |
| `tests/SearchQuality.Chat.Tests` | Unit and regression tests for agent tools, markdown artifacts, compaction transcripts, context token meter, network access, Telegram formatting, and telemetry |
| `tests/SearchQuality.Tests` | Core tests for RRF fusion, lexical query building, `pgvector` literals, chunking, and OpenAI client resilience |
| `docs/chat-studio` | Architecture, operations, and structured exercise logging design documentation |
| `deploy/phoenix` | Self-hosted Arize Phoenix + PostgreSQL Docker Compose recipe, scripts, and backup units |

## Quick Start

### Prerequisites

- .NET 10 SDK (see `global.json`)
- PostgreSQL with the `vector` (`pgvector`) extension enabled
- An OpenAI API key (for `text-embedding-3-large` embeddings) and a LiteLLM proxy or direct OpenAI API key for chat generation

### 1. Configure Credentials

Secrets are never committed to the repository. Provide them via environment variables or create an ignored `appsettings.Local.json` file in the repository root:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=chatstudio;Username=postgres;Password=YOUR_PASSWORD"
  },
  "OpenAI": {
    "ApiKey": "<YOUR_OPENAI_API_KEY>"
  },
  "LiteLLM": {
    "BaseUrl": "http://localhost:4000/v1",
    "ApiKey": "<YOUR_LITELLM_KEY>"
  },
  "Telegram": {
    "BotToken": "OPTIONAL_TELEGRAM_BOT_TOKEN"
  }
}
```

> **Using OpenAI directly without LiteLLM:** By default, `Chat:Provider` in `appsettings.json` is set to `"litellm"`. If you want to use the OpenAI API directly for both embeddings and chat generation without running a LiteLLM proxy, add `"Chat": { "Provider": "openai" }` to your `appsettings.Local.json` (the `LiteLLM` section is then not required).

Alternatively, use environment variables:

- `SEARCHQUALITY_POSTGRES_CONNECTION_STRING`
- `SEARCHQUALITY_OPENAI_API_KEY` (or `OPENAI_API_KEY`)
- `SEARCHQUALITY_LITELLM_BASE_URL` and `SEARCHQUALITY_LITELLM_API_KEY` (when `Chat:Provider` is `litellm`)
- `SEARCHQUALITY_TELEGRAM_BOT_TOKEN` (optional)

### 2. Run the Application

```powershell
dotnet run --project .\src\SearchQuality.Chat
```

Open <http://localhost:5199> in your browser.

> **Language Note:** The built-in Web UI labels and default seeded agent prompts (`DefaultPrompts.cs`) are in Bulgarian. Because agent prompts are stored and versioned in PostgreSQL (`prompt_versions`), you can edit or translate any agent's system prompt (`chat`, `coach`, `compactor`) directly from the **Agents** page (`/agents.html`) at runtime without modifying code.

### 3. Run Tests

```powershell
dotnet test .\ChatStudio.slnx
```

## Documentation

- [Chat Studio Overview](docs/chat-studio/README.md)
- [Architecture — Memory, Agents, Handoff, Deliverables, Diary & Telegram](docs/chat-studio/ARCHITECTURE.md)
- [Operations, Configuration & Deployment](docs/chat-studio/OPERATIONS.md)
- [Structured Exercise Logging Strategy](docs/chat-studio/EXERCISE_LOGGING_STRATEGY.md)
- [Shared Retrieval Core Architecture (Full-Text, pgvector & Hybrid RRF)](docs/search-quality/ARCHITECTURE.md)
- [Phoenix Observability & Self-Hosted Deployment](deploy/phoenix/README.md)
