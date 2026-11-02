# Chat Studio

[Home](../../README.md)

Chat Studio is an ASP.NET Core application for multi-turn conversations with long-term memory, per-user isolation, specialist agents, structured tools, database-versioned prompts, and a Telegram adapter.

It runs as an independent product (`ChatStudio.slnx`) while sharing the `SearchQuality` core assembly for PostgreSQL hybrid retrieval, embeddings, and OpenAI/LiteLLM client infrastructure.

## Quick Start

```powershell
dotnet run --project .\src\SearchQuality.Chat
```

Open <http://localhost:5199>.

## Documentation Index

- [Architecture](ARCHITECTURE.md) — memory model, agents, handoff protocol, deliverables, workout diary, compaction, HTTP API, and Telegram adapter.
- [Operations](OPERATIONS.md) — configuration, provider/model ownership, canonical retrieval strategy IDs, verification checks, and deployment workflow.
- [Phoenix Monitoring](../../deploy/phoenix/README.md) — tracing conversations, model calls, tools, and memory retrieval with OpenInference/OTLP; secure deployment and backups.
- [Structured Exercise Logging Strategy](EXERCISE_LOGGING_STRATEGY.md) — design and evaluation guide for structured workout logging contracts.
- [Retrieval Core Architecture](../search-quality/ARCHITECTURE.md) — details on PostgreSQL Full-Text Search, `pgvector`, and Reciprocal Rank Fusion (RRF) shared by the archive store.

## Runtime Boundary

Chat archive search operates exclusively over the active user's memory (`chat_lab` schema) and returns the top `K` matching archive chunks directly to the active agent without a separate LLM relevance judge gate. Current user documents (`user_artifacts`) and the structured SQL activity log (`activity_log`) are authoritative; archived conversation summaries serve as supplemental, potentially older contextual memory.

Active model selection and prompt versions are runtime state stored in configuration and PostgreSQL (`agents` and `prompt_versions` tables).
