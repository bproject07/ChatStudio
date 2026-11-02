# Chat Studio — Operations

[Chat Studio](README.md) · [Architecture](ARCHITECTURE.md) · [Home](../../README.md)

## Requirements and Configuration

Chat Studio uses:

- PostgreSQL schema `chat_lab` for conversations, agents, prompt versions, user deliverables, archive chunks, and structured activity events;
- LiteLLM or an OpenAI-compatible provider for text generation;
- Direct OpenAI API access for archive memory embeddings (`text-embedding-3-large`);
- Root `appsettings.json` for non-secret defaults.

Primary environment variables:

- `SEARCHQUALITY_POSTGRES_CONNECTION_STRING`
- `SEARCHQUALITY_OPENAI_API_KEY` or `OPENAI_API_KEY`
- `SEARCHQUALITY_LITELLM_BASE_URL` and `SEARCHQUALITY_LITELLM_API_KEY` when `Chat:Provider` is `litellm`
- `SEARCHQUALITY_TELEGRAM_BOT_TOKEN` (optional, enables the Telegram bot hosted service)

Local non-committed overrides can also be placed in `appsettings.Local.json` in the repository root (which is ignored by `.gitignore`) or in an external file referenced via `ExternalConfiguration` (per-field precedence: environment variables → `appsettings.Local.json` → `ExternalConfiguration` → `appsettings.json`). If the configured generation provider is missing required credentials, the application fails fast on startup with a clear error message.

## Running and Verification

```powershell
dotnet run --project .\src\SearchQuality.Chat
dotnet test .\ChatStudio.slnx --no-restore
```

The web interface listens on <http://localhost:5199>.

By default, the host enforces loopback-only access and validates the `Host` header (`localhost`, `127.0.0.1`, `::1`) to prevent DNS rebinding attacks. In an isolated container network, `SEARCHQUALITY_CHAT_PROXY_KEY_FILE` can be configured so non-loopback requests are accepted only when carrying a matching `X-Search-Lab-Proxy` header and a valid localhost `Host` header (the proxy header is stripped before reaching the application pipeline).

When modifying JavaScript files under `wwwroot/js`, run `node --check` on every changed `.js` file and bump the `?v=YYYYMMDD-N` cache-busting query string in all HTML files referencing the asset.

## Models and Prompts

- `Chat:Model` and `Chat:Provider` in `appsettings.json` (or `appsettings.Local.json`) define initial defaults when seeding a new database.
- Each agent persists its own provider, model, and active prompt version in PostgreSQL (`chat_lab.agents`).
- The model dropdown in the Web UI is populated dynamically from the provider's `/models` endpoint (falling back to the configured model if unreachable).
- Editing a prompt creates a new immutable row in `prompt_versions`; rolling back activates a previous version without mutating historical messages.

## Retrieval Strategies

Supported canonical strategy IDs:

- `postgres-hybrid` (Reciprocal Rank Fusion of Full-Text + `pgvector`)
- `postgres-pgvector` (vector cosine similarity)
- `postgres-full-text` (PostgreSQL GIN `tsvector` / `ts_rank_cd`)

These exact IDs are used in configuration and API requests. Shortened names such as `pgvector` or `full-text` are not valid strategy IDs.

Chat archive retrieval returns the top `K` archive chunks for the current user directly to the model without an intermediate LLM judge filter.

## User State and Backup Boundaries

Before deployment or database maintenance, back up both the application configuration and the PostgreSQL database. The following data represents persistent user state rather than disposable build artifacts:

- Sessions and conversation messages (`sessions`, `messages`);
- Agent definitions and immutable prompt versions (`agents`, `prompt_versions`);
- Current user documents and their version history (`user_artifacts`, `documents`, `chunks`);
- Append-only notes and structured workout/activity logs (`artifact_notes`, `activity_log`);
- Markdown archives under `data/chat-history/` and deliverable files under `data/artifacts/`.

Never delete these tables or directories during routine deployments or cleanups.

## Standard Deployment Workflow

1. Run the full test suite against the exact source commit (`dotnet test .\ChatStudio.slnx`).
2. Back up the PostgreSQL database and persistent data directories.
3. Publish the application for the target runtime/architecture:

   ```powershell
   dotnet publish .\src\SearchQuality.Chat -c Release -r linux-arm64 --self-contained false
   ```

   *(Adjust `-r` to match your target host architecture, e.g., `linux-x64` or `linux-arm64`.)*
4. Replace the deployed binaries atomically or with a reversible symlink/directory swap.
5. Restart the managed service.
6. Activate new prompt versions only if required by the release plan.
7. Verify health/API responsiveness and perform a read-only conversation check.

## Phoenix Monitoring

OpenTelemetry / OpenInference tracing is configured exclusively via server environment variables and is disabled by default. Restart the Chat Studio service after changing any telemetry variable:

| Environment Variable | Behavior |
|---|---|
| `CHAT_TELEMETRY_ENABLED` | Set to `true` to enable tracing; disabled by default |
| `CHAT_TELEMETRY_CAPTURE_CONTENT` | Set to `true` to record prompts, messages, and tool payloads; metadata-only by default |
| `CHAT_TELEMETRY_ENDPOINT` | Required `/v1/traces` OTLP HTTP endpoint (must be `https://` or loopback `http://`) |
| `CHAT_TELEMETRY_API_KEY_FILE` | Path to a private file containing the collector API key (takes precedence over `CHAT_TELEMETRY_API_KEY`) |
| `CHAT_TELEMETRY_API_KEY` | Collector API key from secret environment |
| `CHAT_TELEMETRY_PROJECT` | Phoenix project name; defaults to `chat-studio` |
| `CHAT_TELEMETRY_MAX_CONTENT_CHARACTERS` | Maximum characters per captured content attribute (`1024`–`4000000`), truncating with an explicit marker |

For long conversations, also set standard OpenTelemetry attribute limits (`OTEL_SPAN_ATTRIBUTE_COUNT_LIMIT=4096`, `OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT=4000000`) as shown in the [Phoenix deployment guide](../../deploy/phoenix/README.md). An invalid endpoint or missing key when telemetry is enabled causes startup validation to fail fast. At runtime, if a validly configured collector becomes unreachable, background export drops spans gracefully without blocking chat turns.

## Telegram Adapter

Telegram runs as a hosted background service over the same `ChatService`:

- Verify that `channel_links` map Telegram chat IDs to the intended `user_id` profiles;
- Commands `/start`, `/unlink`, `/new`, and `/back` manage user linking, session reset, and specialist handback;
- Workout diary confirmations are generated from actual executed SQL tool operations rather than unverified model prose;
- Markdown responses are converted into safe Telegram HTML and split cleanly under Telegram's message length limit.
