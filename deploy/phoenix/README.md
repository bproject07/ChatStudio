# Phoenix for Chat Studio

[Chat Operations](../../docs/chat-studio/OPERATIONS.md) · [Architecture](../../docs/chat-studio/ARCHITECTURE.md)

Standalone Arize Phoenix with PostgreSQL and native email/password authentication. Chat Studio sends OpenInference/OTLP traces directly from the application code while LiteLLM remains the model provider. This links every model invocation with the actually executed tools, archive retrieval, and persistence of the response. Do not enable a parallel LiteLLM Phoenix callback for the same requests; otherwise a second, disconnected copy of the model calls will be recorded.

## Access Boundaries

```text
browser → Cloudflare proxy → HTTPS Nginx (Cloudflare IPs only) → Phoenix login
Chat Studio → authenticated OTLP → 127.0.0.1:6006 → Phoenix → internal PostgreSQL
```

- The only published Docker port is `127.0.0.1:6006`.
- PostgreSQL, OTLP/gRPC, and metrics have no published host ports.
- Nginx verifies the original TCP peer before trusting the restored client IP. A spoofed `CF-Connecting-IP` header does not grant direct access to the origin.
- `/v1/traces`, `/metrics`, and `/openapi.json` are blocked on the public reverse proxy. The internal OTLP endpoint also requires an API key.
- Phoenix requires login to access data; the static login application is public. Sessions use `Secure`/`HttpOnly` cookies. The reverse proxy disables caching and rate-limits `/auth/` requests.
- The collector uses a separate `MEMBER` account. Its API key has no administrative user-management privileges, though it has access to project data; it is not a write-only or project-scoped credential.
- Cloudflare Access is an optional, separate layer. DNS API permissions alone do not configure an Access policy. This setup does not assume that Cloudflare Access or Cloudflare Tunnel is enabled.

## Initial Installation

Commands target Linux with Docker Compose, Nginx, and Python 3. The example deployment directory is `/opt/chat-phoenix`; adapt the systemd unit files if you use a different path.

1. Copy the files from this directory to the server. Restrict the deployment directory to the administrator. Run once:

   ```sh
   sudo python3 /opt/chat-phoenix/initialize.py --directory /opt/chat-phoenix --public-url https://phoenix.example.org
   sudo docker compose -f /opt/chat-phoenix/compose.yaml config --quiet
   sudo docker compose -f /opt/chat-phoenix/compose.yaml up -d
   ```

   `initialize.py` refuses to overwrite existing secrets. The password for `admin@localhost` is written to `secrets/admin-password`. Secret files are created with mode `0600` and the directory with `0700`. Never publish these files or an expanded Compose configuration.

2. Once Phoenix is healthy, create the collector key and remove the temporary administrative bearer token:

   ```sh
   sudo python3 /opt/chat-phoenix/bootstrap-collector.py --directory /opt/chat-phoenix
   sudo python3 /opt/chat-phoenix/finalize.py --directory /opt/chat-phoenix
   sudo docker compose -f /opt/chat-phoenix/compose.yaml up -d --force-recreate phoenix
   ```

   The script creates a local service account and sends no email. The bootstrap token is revoked when the container is recreated. Preserve the signing secret and PostgreSQL data across restarts; never run `down -v`.

3. Issue a valid TLS certificate via DNS-01 before publishing the DNS record. Certbot DNS credentials remain in the server's protected secret store.

   ```sh
   sudo python3 /opt/chat-phoenix/configure-proxy.py --hostname phoenix.example.org
   sudo nginx -t
   sudo systemctl reload nginx
   ```

   The script fetches the current IP ranges from the Cloudflare API and rejects incomplete responses. It creates only the Phoenix site/snippet and a dedicated `geo` map, using the certificate under `/etc/letsencrypt/live/<hostname>/`. To refresh Cloudflare IP ranges later, re-run the script, `nginx -t`, and reload.

4. Create a proxied DNS record pointing to the origin. Verify that Cloudflare uses HTTPS to the origin. For a dedicated new zone, prefer `Full (strict)`; in a shared zone, verify the impact on other services before changing global SSL/TLS mode. Adapt the hostname in [renew-nginx.sh](renew-nginx.sh) and install it as executable in `/etc/letsencrypt/renewal-hooks/deploy/` so renewed certificates reload Nginx automatically.

5. Copy `secrets/collector-key` to a private file readable only by the Chat service user. Pass the following non-secret settings via a protected service environment file:

   ```ini
   CHAT_TELEMETRY_ENABLED=true
   CHAT_TELEMETRY_CAPTURE_CONTENT=true
   CHAT_TELEMETRY_ENDPOINT=http://127.0.0.1:6006/v1/traces
   CHAT_TELEMETRY_API_KEY_FILE=/path/to/private/collector-key
   CHAT_TELEMETRY_PROJECT=chat-studio
   CHAT_TELEMETRY_MAX_CONTENT_CHARACTERS=2000000
   OTEL_SPAN_ATTRIBUTE_COUNT_LIMIT=4096
   OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT=4000000
   ```

   The endpoint must be `/v1/traces` over HTTPS or loopback HTTP. The key is never exposed through the public settings API. Restart Chat Studio after changing these variables. For initial verification, use an isolated copy of the Chat database, a separate Phoenix project, and disable Telegram polling.

## Acceptance Checks

- Without logging in, `GET /v1/projects` returns `401`; an invalid password does not create a session. A successful native login loads projects and sets `Secure`/`HttpOnly` cookies.
- `POST /v1/traces` returns `404` through the public reverse proxy and `401` locally without a key.
- A direct HTTPS connection to the origin with the same `Host`/SNI returns `403`, including when a client-supplied `CF-Connecting-IP` header is sent.
- `ss -lnt` and Docker port mappings show only loopback bindings for Phoenix; external TCP checks cannot reach ports `6006`, `4317`, or `9090`.
- A real read-only conversation appears in the selected project with a linked root span, model/tool/retrieval child spans, `session.id`, prompt version, and token usage.
- In the isolated database, verify handoff, rejected and successful diary writes, compaction, and streaming. `chat.persistence.committed=true` indicates that turn persistence completed; tool output shows the result of each individual operation.
- Compare trace content against known runtime credentials without printing them. Authentication headers are never recorded.
- A temporarily unavailable collector must not interrupt chat operations.

## Backup, Retention, and Restore

Compose configures a 30-day default retention policy for newly created Phoenix projects. Verify the effective policy in Phoenix for existing projects. This is an operational setting; it does not delete the original Chat database or archives.

[backup.py](backup.py) creates a consistent `pg_dump -Fc` and an archive of the Compose configuration and secrets. It writes completed backups atomically, restricts permissions to the owner, and prunes only its own dated directories after the retention window expires. Install [phoenix-backup.service](phoenix-backup.service) and [phoenix-backup.timer](phoenix-backup.timer) in `/etc/systemd/system/`:

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now phoenix-backup.timer
sudo systemctl start phoenix-backup.service
sudo systemctl status phoenix-backup.service --no-pager
```

Backups are stored under `/opt/chat-phoenix/backups/` with a 7-day local retention window. They contain personal conversations and credentials. Keep an off-server copy in an appropriately secured backup store; a local systemd timer alone does not protect against host failure. Monitor free disk space, database size, and failed backup units.

When restoring, use a **separate, stopped Phoenix deployment**: restore the private configuration and `phoenix.dump` via `pg_restore` into an empty database running the same PostgreSQL major version, then start the same Phoenix version and verify login and traces. The signing secret and collector key file must match the restored database. Never restore on top of the active Chat database.

Before upgrading Phoenix, back up the database and review the release notes and schema migrations. Rolling back only the Docker image does not undo an already-applied schema migration. To pause telemetry temporarily, set `CHAT_TELEMETRY_ENABLED=false` and restart Chat; user data and existing Phoenix history are preserved.

## How to Read Monitoring

In the Phoenix project, open **Traces** and expand `chat.turn`; **Sessions** groups turns by `session.id`. `chat.channel` distinguishes `web` and `telegram`. Model spans contain the system prompt, conversation messages, tool schemas, model output, and token usage; tool spans contain arguments, results, and rejections; retrieval spans contain chunks and scores; compaction spans contain the input conversation, generated summary, and next session ID.

Content capture is disabled by default in the application. When `CHAT_TELEMETRY_CAPTURE_CONTENT=true` is enabled, Phoenix stores a second copy of personal conversation content. Known runtime credentials and recognizable secret tokens are redacted, though this is not a universal PII filter. The character limit applies per content value; truncated values are marked with `chat.content_truncated` and `[TRUNCATED]`. Only new operations are traced; there is no automatic backfill of historical turns.

Token counts are recorded on individual LLM/embedding spans to avoid double counting. For JSON responses, available cached and reasoning token counts are also recorded. Streaming turns record the assembled response and primary token usage without duplicating the raw SSE chunk stream. Embedding vectors and internal provider fields not returned by the API are not recorded. Cost estimates for custom LiteLLM model aliases require a verified pricing mapping in Phoenix; token usage alone does not prove actual billed cost. Automated quality evaluations and alerts are not triggered automatically by tracing.

Trace export runs in the background with a bounded queue and short timeout. During prolonged collector outages, queue saturation, or abrupt process termination, spans may be dropped. Phoenix is a diagnostic tool; the authoritative record of user operations remains in PostgreSQL.

Official references: [Phoenix deployment](https://arize.com/docs/phoenix/self-hosting), [Phoenix authentication](https://arize.com/docs/phoenix/self-hosting/features/authentication), [Cloudflare IP ranges](https://www.cloudflare.com/ips/).
