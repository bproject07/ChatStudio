using System.Globalization;
using Npgsql;
using SearchQuality.Configuration;
using SearchQuality.Corpus;
using SearchQuality.Domain;
using SearchQuality.OpenAI;
using SearchQuality.Postgres;
using SearchQuality.Chat.Observability;

namespace SearchQuality.Chat;

/// <summary>
/// Архивът на чата: собствена PostgreSQL схема (по подразбиране chat_lab) със
/// същата структура като лабораторната, плюс document_type и user_id. Записът е
/// само добавящ (append). Търсенето е собствено (не през лабораторния store),
/// защото ВСЯКА заявка филтрира по потребител — паметта на един потребител
/// никога не изтича в чата на друг.
/// </summary>
public sealed partial class ChatArchiveStore
{
    private readonly PostgresSearchStore _store;
    private readonly PostgresSettings _postgres;
    private readonly OpenAiApiClient _openAiClient;
    private readonly LabSettings _labSettings;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly string _seedModel;
    private bool _initialized;
    private readonly ChatTelemetry _telemetry;

    public ChatArchiveStore(LabSettings labSettings, ChatSettings chatSettings, OpenAiApiClient openAiClient,
        ChatTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(labSettings);
        ArgumentNullException.ThrowIfNull(chatSettings);
        ArgumentNullException.ThrowIfNull(openAiClient);

        _labSettings = labSettings;
        _openAiClient = openAiClient;
        _telemetry = telemetry ?? ChatTelemetry.Disabled;
        _seedModel = chatSettings.Model;
        _postgres = labSettings.Postgres with { Schema = chatSettings.Schema };
        _store = new PostgresSearchStore(_postgres);
    }

    private Task<EmbeddingResult> CreateEmbeddingsAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken) =>
        _telemetry.TraceAsync("memory.embed", "EMBEDDING", async span =>
        {
            span.Tag("embedding.model_name", _labSettings.OpenAI.EmbeddingModel)
                .Tag("chat.embedding.input_count", inputs.Count).Json("input.value", inputs)
                .Tag("input.mime_type", "application/json");
            var result = await _openAiClient.CreateEmbeddingsAsync(inputs, cancellationToken);
            span.Usage(result.Usage).Tag("chat.embedding.vector_count", result.Vectors.Count)
                .Tag("chat.embedding.dimensions", result.Vectors.FirstOrDefault()?.Length ?? 0);
            return result;
        });

    public async Task<RetrievalResult> SearchAsync(
        string userId,
        string query,
        string strategy,
        int topK,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await EnsureInitializedAsync(cancellationToken);

        switch (strategy)
        {
            case "postgres-full-text":
                return new RetrievalResult(
                    await SearchFullTextAsync(userId, query, topK, cancellationToken),
                    TokenUsage.Empty);
            case "postgres-pgvector":
                {
                    var embedding = await CreateEmbeddingsAsync([query], cancellationToken);
                    var hits = await SearchVectorAsync(userId, embedding.Vectors[0], topK, cancellationToken);
                    return new RetrievalResult(hits, embedding.Usage);
                }
            default:
                {
                    // Hybrid: широк пул от двете търсения + RRF — същата механика
                    // като лабораторния PostgresHybridRetriever.
                    var candidateCount = Math.Min(100, Math.Max(20, topK * 4));
                    var lexicalTask = SearchFullTextAsync(userId, query, candidateCount, cancellationToken);
                    var embeddingTask = CreateEmbeddingsAsync([query], cancellationToken);
                    await Task.WhenAll(lexicalTask, embeddingTask);
                    var vectorHits = await SearchVectorAsync(
                        userId,
                        (await embeddingTask).Vectors[0],
                        candidateCount,
                        cancellationToken);
                    var fused = ReciprocalRankFusion.Fuse(await lexicalTask, vectorHits, topK);
                    return new RetrievalResult(fused, (await embeddingTask).Usage);
                }
        }
    }

    /// <summary>Chunk-ва, векторизира и ДОБАВЯ документ на конкретния потребител
    /// ('chat' = архивиран разговор, 'artifact' = записан резултат). И двата
    /// вида влизат в един индекс, но се четат в отделни слоеве: датирана чат
    /// памет и текущи версии през user_artifacts.</summary>
    public async Task ArchiveAsync(
        string userId,
        SourceDocument document,
        CancellationToken cancellationToken,
        string documentType = "chat")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(document);
        await EnsureInitializedAsync(cancellationToken);

        var chunker = new ApproximateTokenChunker(
            _labSettings.Benchmark.ChunkSizeWords,
            _labSettings.Benchmark.ChunkOverlapWords);
        var chunks = chunker.Chunk(document).ToArray();
        var embeddings = await CreateEmbeddingsAsync(
            chunks.Select(chunk => $"{chunk.DocumentTitle}\n{chunk.Text}").ToArray(),
            cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insertDocument = new NpgsqlCommand(
                         $"""
                         INSERT INTO {_postgres.Schema}.documents
                             (id, title, file_name, content, document_type, created_at, user_id)
                         VALUES (@id, @title, @file_name, @content, @type, now(), @user_id);
                         """,
                         connection,
                         transaction))
        {
            insertDocument.Parameters.AddWithValue("type", documentType);
            insertDocument.Parameters.AddWithValue("id", document.Id);
            insertDocument.Parameters.AddWithValue("title", document.Title);
            insertDocument.Parameters.AddWithValue("file_name", document.FileName);
            insertDocument.Parameters.AddWithValue("content", document.Content);
            insertDocument.Parameters.AddWithValue("user_id", userId);
            await insertDocument.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < chunks.Length; index++)
        {
            await using var insertChunk = new NpgsqlCommand(
                $"""
                INSERT INTO {_postgres.Schema}.chunks
                    (id, document_id, document_title, ordinal, content, embedding, user_id)
                VALUES
                    (@id, @document_id, @document_title, @ordinal, @content, CAST(@embedding AS vector), @user_id);
                """,
                connection,
                transaction);
            insertChunk.Parameters.AddWithValue("id", chunks[index].Id);
            insertChunk.Parameters.AddWithValue("document_id", chunks[index].DocumentId);
            insertChunk.Parameters.AddWithValue("document_title", chunks[index].DocumentTitle);
            insertChunk.Parameters.AddWithValue("ordinal", chunks[index].Ordinal);
            insertChunk.Parameters.AddWithValue("content", chunks[index].Text);
            insertChunk.Parameters.AddWithValue("embedding", PgVectorLiteral.Format(embeddings.Vectors[index]));
            insertChunk.Parameters.AddWithValue("user_id", userId);
            await insertChunk.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArchiveDescriptor>> ListArchivesAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT id, title, file_name, coalesce(created_at, now()), length(content)
            FROM {_postgres.Schema}.documents
            WHERE document_type = 'chat' AND user_id = @user_id
            ORDER BY created_at DESC;
            """,
            connection);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var archives = new List<ArchiveDescriptor>();
        while (await reader.ReadAsync(cancellationToken))
        {
            archives.Add(new ArchiveDescriptor(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTime>(3).ToUniversalTime(),
                reader.GetInt32(4)));
        }

        return archives;
    }

    public async Task<string?> GetArchiveContentAsync(
        string userId,
        string id,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT content FROM {_postgres.Schema}.documents
            WHERE id = @id AND document_type = 'chat' AND user_id = @user_id;
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user_id", userId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<bool> HasArchivesAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {_postgres.Schema}.chunks WHERE user_id = @user_id);",
            connection);
        command.Parameters.AddWithValue("user_id", userId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<IReadOnlyList<SearchHit>> SearchFullTextAsync(
        string userId,
        string query,
        int topK,
        CancellationToken cancellationToken)
    {
        var lexicalQuery = LexicalQueryBuilder.Build(query);
        if (string.IsNullOrWhiteSpace(lexicalQuery))
        {
            return [];
        }

        // Този слой е само датираната памет (резюметата на чатове).
        // Текущите версии на артефактите се търсят отделно и с отделен
        // строг бюджет; целият документ остава достъпен с get_deliverable.
        var sql = $"""
            SELECT c.document_id, c.document_title, c.id, c.content,
                   (ts_rank_cd(c.search_vector, to_tsquery('simple', @query)))::double precision AS score
            FROM {_postgres.Schema}.chunks c
            JOIN {_postgres.Schema}.documents d ON d.id = c.document_id
            WHERE c.user_id = @user_id
              AND d.document_type = 'chat'
              AND c.search_vector @@ to_tsquery('simple', @query)
            ORDER BY score DESC, c.document_id, c.ordinal
            LIMIT @top_k;
            """;

        return await ExecuteSearchAsync(sql, userId, lexicalQuery, null, topK, cancellationToken);
    }

    private async Task<IReadOnlyList<SearchHit>> SearchVectorAsync(
        string userId,
        IReadOnlyList<float> embedding,
        int topK,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT c.document_id, c.document_title, c.id, c.content,
                   (1 - (c.embedding <=> CAST(@embedding AS vector)))::double precision AS score
            FROM {_postgres.Schema}.chunks c
            JOIN {_postgres.Schema}.documents d ON d.id = c.document_id
            WHERE c.user_id = @user_id
              AND d.document_type = 'chat'
              AND c.embedding IS NOT NULL
            ORDER BY c.embedding <=> CAST(@embedding AS vector), c.document_id, c.ordinal
            LIMIT @top_k;
            """;

        return await ExecuteSearchAsync(
            sql,
            userId,
            query: "-",
            PgVectorLiteral.Format(embedding),
            topK,
            cancellationToken);
    }

    private async Task<IReadOnlyList<SearchHit>> ExecuteSearchAsync(
        string sql,
        string userId,
        string query,
        string? embedding,
        int topK,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("top_k", topK);
        if (sql.Contains("@query", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("query", query);
        }

        if (embedding is not null)
        {
            command.Parameters.AddWithValue("embedding", embedding);
        }

        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new SearchHit(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture)));
        }

        return hits;
    }

    /// <summary>
    /// Seed при първо стартиране: агентът се създава с v1 = сегашния хардкоднат
    /// промпт. Съществуващи агенти НЕ се пипат — истината е в базата.
    /// </summary>
    private async Task SeedAgentAsync(
        NpgsqlConnection connection,
        string key,
        string displayName,
        string kind,
        string instructions,
        string? activation,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(
                         $"SELECT EXISTS (SELECT 1 FROM {_postgres.Schema}.agents WHERE key = @key);",
                         connection))
        {
            exists.Parameters.AddWithValue("key", key);
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                return;
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var insertAgent = new NpgsqlCommand(
                         $"""
                         INSERT INTO {_postgres.Schema}.agents (key, display_name, kind, provider, model, activation)
                         VALUES (@key, @display_name, @kind, @provider, @model, @activation);
                         """,
                         connection,
                         transaction))
        {
            insertAgent.Parameters.AddWithValue("key", key);
            insertAgent.Parameters.AddWithValue("display_name", displayName);
            insertAgent.Parameters.AddWithValue("kind", kind);
            insertAgent.Parameters.AddWithValue("provider", "litellm");
            insertAgent.Parameters.AddWithValue("model", _seedModel);
            insertAgent.Parameters.AddWithValue("activation", (object?)activation ?? DBNull.Value);
            await insertAgent.ExecuteNonQueryAsync(cancellationToken);
        }

        long versionId;
        await using (var insertVersion = new NpgsqlCommand(
                         $"""
                         INSERT INTO {_postgres.Schema}.prompt_versions (agent_key, version, instructions, note)
                         VALUES (@key, 1, @instructions, 'начална версия (seed)')
                         RETURNING id;
                         """,
                         connection,
                         transaction))
        {
            insertVersion.Parameters.AddWithValue("key", key);
            insertVersion.Parameters.AddWithValue("instructions", instructions);
            versionId = (long)(await insertVersion.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var activate = new NpgsqlCommand(
                         $"UPDATE {_postgres.Schema}.agents SET active_prompt_version_id = @vid WHERE key = @key;",
                         connection,
                         transaction))
        {
            activate.Parameters.AddWithValue("vid", versionId);
            activate.Parameters.AddWithValue("key", key);
            await activate.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Модел + активен промпт на агент — чете се на всяко извикване, без кеш.</summary>
    public async Task<AgentRuntime> GetAgentRuntimeAsync(string key, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT a.display_name, a.provider, a.model, p.instructions, p.version
            FROM {_postgres.Schema}.agents a
            JOIN {_postgres.Schema}.prompt_versions p ON p.id = a.active_prompt_version_id
            WHERE a.key = @key;
            """,
            connection);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Агент '{key}' липсва или няма активна версия на промпта.");
        }

        return new AgentRuntime(
            key,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4));
    }

    public async Task<IReadOnlyList<AgentInfo>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT a.key, a.display_name, a.kind, a.enabled, a.provider, a.model, p.version, a.activation
            FROM {_postgres.Schema}.agents a
            LEFT JOIN {_postgres.Schema}.prompt_versions p ON p.id = a.active_prompt_version_id
            ORDER BY a.kind, a.key;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var agents = new List<AgentInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            agents.Add(new AgentInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetString(7)));
        }

        return agents;
    }

    /// <summary>Създава нов специалист + v1 на промпта му, в една транзакция.</summary>
    public async Task CreateAgentAsync(
        string key,
        string displayName,
        string activation,
        string model,
        string instructions,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insertAgent = new NpgsqlCommand(
                         $"""
                         INSERT INTO {_postgres.Schema}.agents (key, display_name, kind, provider, model, activation)
                         VALUES (@key, @display_name, 'specialist', 'litellm', @model, @activation);
                         """,
                         connection,
                         transaction))
        {
            insertAgent.Parameters.AddWithValue("key", key);
            insertAgent.Parameters.AddWithValue("display_name", displayName);
            insertAgent.Parameters.AddWithValue("model", model);
            insertAgent.Parameters.AddWithValue("activation", activation);
            await insertAgent.ExecuteNonQueryAsync(cancellationToken);
        }

        long versionId;
        await using (var insertVersion = new NpgsqlCommand(
                         $"""
                         INSERT INTO {_postgres.Schema}.prompt_versions (agent_key, version, instructions, note)
                         VALUES (@key, 1, @instructions, 'начална версия')
                         RETURNING id;
                         """,
                         connection,
                         transaction))
        {
            insertVersion.Parameters.AddWithValue("key", key);
            insertVersion.Parameters.AddWithValue("instructions", instructions);
            versionId = (long)(await insertVersion.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var activate = new NpgsqlCommand(
                         $"UPDATE {_postgres.Schema}.agents SET active_prompt_version_id = @vid WHERE key = @key;",
                         connection,
                         transaction))
        {
            activate.Parameters.AddWithValue("vid", versionId);
            activate.Parameters.AddWithValue("key", key);
            await activate.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Кой държи микрофона на сесията (NULL = оркестраторът).</summary>
    public async Task SetActiveAgentAsync(
        Guid sessionId,
        string? agentKey,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"UPDATE {_postgres.Schema}.sessions SET active_agent = @agent WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("agent", (object?)agentKey ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Стойност от app_state (напр. telegram_offset); null = няма.</summary>
    public async Task<string?> GetStateValueAsync(string key, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT value FROM {_postgres.Schema}.app_state WHERE key = @key;", connection);
        command.Parameters.AddWithValue("key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetStateValueAsync(string key, string value, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_postgres.Schema}.app_state (key, value) VALUES (@key, @value)
            ON CONFLICT (key) DO UPDATE SET value = @value, updated_at = now();
            """,
            connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Нашият потребител зад външен канал; null = още не е свързан.</summary>
    public async Task<string?> GetChannelUserAsync(
        string channel, string externalId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT user_id FROM {_postgres.Schema}.channel_links WHERE channel = @channel AND external_id = @external;",
            connection);
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("external", externalId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task LinkChannelUserAsync(
        string channel, string externalId, string userId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_postgres.Schema}.channel_links (channel, external_id, user_id)
            VALUES (@channel, @external, @user)
            ON CONFLICT (channel, external_id) DO UPDATE SET user_id = @user, linked_at = now();
            """,
            connection);
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("external", externalId);
        command.Parameters.AddWithValue("user", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UnlinkChannelUserAsync(
        string channel, string externalId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {_postgres.Schema}.channel_links WHERE channel = @channel AND external_id = @external;",
            connection);
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("external", externalId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Премества стрелката „текущото от този вид" към нов документ.</summary>
    public async Task SetCurrentArtifactAsync(
        string userId, string kind, string documentId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_postgres.Schema}.user_artifacts (user_id, kind, document_id)
            VALUES (@user, @kind, @document)
            ON CONFLICT (user_id, kind) DO UPDATE SET document_id = @document, updated_at = now();
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("document", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Текущият артефакт от даден вид — дословно, без търсене.</summary>
    public async Task<CurrentArtifact?> GetCurrentArtifactAsync(
        string userId, string kind, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT d.id, d.title, d.content, a.updated_at
            FROM {_postgres.Schema}.user_artifacts a
            JOIN {_postgres.Schema}.documents d ON d.id = a.document_id
            WHERE a.user_id = @user AND a.kind = @kind;
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("kind", kind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CurrentArtifact(
                reader.GetString(0), kind, reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    /// <summary>Списъкът „какво има потребителят" — за евтино инжектиране в контекста.</summary>
    public async Task<IReadOnlyList<ArtifactSummary>> ListCurrentArtifactsAsync(
        string userId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT a.kind, d.title, a.updated_at
            FROM {_postgres.Schema}.user_artifacts a
            JOIN {_postgres.Schema}.documents d ON d.id = a.document_id
            WHERE a.user_id = @user
            ORDER BY a.kind;
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);

        var result = new List<ArtifactSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ArtifactSummary(
                reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return result;
    }

    public async Task AddArtifactNoteAsync(
        string userId, string kind, string note, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_postgres.Schema}.artifact_notes (user_id, kind, note) VALUES (@user, @kind, @note);",
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("note", note);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArtifactNote>> GetArtifactNotesAsync(
        string userId, string kind, int limit, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT note, created_at FROM {_postgres.Schema}.artifact_notes
            WHERE user_id = @user AND kind = @kind
            ORDER BY id DESC LIMIT @limit;
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("limit", limit);

        var result = new List<ArtifactNote>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ArtifactNote(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1)));
        }

        result.Reverse(); // хронологично, най-старата първа
        return result;
    }

    /// <summary>Запис в дневника; при корекция старият ред получава superseded_by.
    /// Двете писания са в една транзакция — прекъсване между тях иначе оставя
    /// и стария, и новия ред активни (дубликат в статистиката).
    /// happened_at: изрична дата (happened_on, обед по Europe/Sofia) > датата
    /// на коригирания ред > now(). Корекцията поправя съдържание, не време —
    /// иначе „последната тренировка“ скача на датата на корекцията.</summary>
    public async Task<long> LogActivityAsync(
        string userId, string kind, string label, string? dataJson, string? note,
        long? replacesId, DateOnly? happenedOn, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var id = await InsertActivityAsync(
            connection,
            transaction,
            userId,
            kind,
            label,
            dataJson,
            note,
            replacesId,
            happenedOn,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    /// <summary>
    /// Атомарен batch вариант: всички редове и корекции се commit-ват заедно
    /// или нито един не остава при грешка.
    /// </summary>
    public async Task<IReadOnlyList<long>> LogActivitiesAsync(
        string userId,
        IReadOnlyList<PersonalDataTools.LogArgs> logs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentOutOfRangeException.ThrowIfLessThan(logs.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(logs.Count, 20);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var ids = new List<long>(logs.Count);
        foreach (var log in logs)
        {
            ids.Add(await InsertActivityAsync(
                connection,
                transaction,
                userId,
                log.Kind,
                log.Label,
                log.DataJson,
                log.Note,
                log.ReplacesId,
                log.HappenedOn,
                cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    private async Task<long> InsertActivityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string userId,
        string kind,
        string label,
        string? dataJson,
        string? note,
        long? replacesId,
        DateOnly? happenedOn,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? replacedAt = null;
        if (replacesId is { } replacementTarget)
        {
            await using var target = new NpgsqlCommand(
                $"""
                SELECT happened_at
                FROM {_postgres.Schema}.activity_log
                WHERE id = @id AND user_id = @user AND superseded_by IS NULL
                FOR UPDATE;
                """,
                connection,
                transaction);
            target.Parameters.AddWithValue("id", replacementTarget);
            target.Parameters.AddWithValue("user", userId);
            await using var reader = await target.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InactiveActivityReplacementException(replacementTarget);
            }

            replacedAt = reader.GetFieldValue<DateTimeOffset>(0);
        }

        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {_postgres.Schema}.activity_log (user_id, kind, label, data, note, happened_at)
            VALUES (@user, @kind, @label, CAST(@data AS jsonb), @note,
                    coalesce(
                        ((@happened_on)::timestamp + interval '12 hours') AT TIME ZONE 'Europe/Sofia',
                        @replaced_at,
                        now()))
            RETURNING id;
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("user", userId);
        insert.Parameters.AddWithValue("kind", kind);
        insert.Parameters.AddWithValue("label", label);
        insert.Parameters.AddWithValue("data", (object?)dataJson ?? DBNull.Value);
        insert.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
        insert.Parameters.Add(new NpgsqlParameter("happened_on", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = happenedOn is { } day
                ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : DBNull.Value
        });
        insert.Parameters.Add(new NpgsqlParameter(
            "replaced_at",
            NpgsqlTypes.NpgsqlDbType.TimestampTz)
        {
            Value = (object?)replacedAt ?? DBNull.Value
        });
        var id = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;

        if (replacesId is { } old)
        {
            await using var supersede = new NpgsqlCommand(
                $"""
                UPDATE {_postgres.Schema}.activity_log SET superseded_by = @new
                WHERE id = @old AND user_id = @user AND superseded_by IS NULL;
                """,
                connection,
                transaction);
            supersede.Parameters.AddWithValue("new", id);
            supersede.Parameters.AddWithValue("old", old);
            supersede.Parameters.AddWithValue("user", userId);
            if (await supersede.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InactiveActivityReplacementException(old);
            }
        }

        return id;
    }

    /// <summary>Действащ запис за същото нещо от целевия ден — защитата срещу
    /// дублаж. Денят е по местното време на потребителя (Europe/Sofia), не UTC
    /// — иначе запис в 01:30 местно попада в „вчера" и дублажът минава.
    /// happenedOn = null значи днес; запис за минал ден се сверява със СВОЯ
    /// ден, не с днешния — иначе миналият дубликат минава, а валиден минал
    /// запис се отказва заради днешен със същия етикет.</summary>
    public async Task<ActivityEntry?> GetSameDayEntryAsync(
        string userId, string kind, string label, DateOnly? happenedOn,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT id, kind, label, data::text, note, happened_at
            FROM {_postgres.Schema}.activity_log
            WHERE user_id = @user AND kind = @kind AND lower(label) = lower(@label)
              AND superseded_by IS NULL
              AND (happened_at AT TIME ZONE 'Europe/Sofia')::date
                  = coalesce((@target)::date, (now() AT TIME ZONE 'Europe/Sofia')::date)
            ORDER BY id DESC LIMIT 1;
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("label", label);
        command.Parameters.Add(new NpgsqlParameter("target", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = happenedOn is { } day
                ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : DBNull.Value
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ActivityEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    /// <summary>
    /// Анулира активен погрешен ред, без физическо изтриване. Самореференцията
    /// в superseded_by е tombstone: всички оперативни заявки и статистики
    /// четат само NULL, а първоначалният ред и причината остават за одит.
    /// </summary>
    public async Task<string?> VoidActivityAsync(
        string userId,
        long id,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentOutOfRangeException.ThrowIfLessThan(id, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_postgres.Schema}.activity_log
            SET superseded_by = id,
                note = concat_ws(
                    ' | ',
                    nullif(note, ''),
                    '[анулиран] ' || @reason)
            WHERE id = @id
              AND user_id = @user
              AND superseded_by IS NULL
            RETURNING label;
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("reason", reason.Trim());
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>Действащите записи (без заменените), най-новите първи.</summary>
    public async Task<IReadOnlyList<ActivityEntry>> GetActivityLogAsync(
        string userId,
        string? kind,
        int days,
        DateOnly? happenedOn,
        int limit,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT id, kind, label, data::text, note, happened_at
            FROM {_postgres.Schema}.activity_log
            WHERE user_id = @user
              AND superseded_by IS NULL
              AND (
                    (@target IS NULL
                     AND happened_at > now() - make_interval(days => @days))
                    OR
                    (@target IS NOT NULL
                     AND (happened_at AT TIME ZONE 'Europe/Sofia')::date
                         = (@target)::date)
                  )
              AND (@kind IS NULL OR kind = @kind)
            ORDER BY happened_at DESC
            LIMIT @limit;
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)kind ?? DBNull.Value
        });
        command.Parameters.AddWithValue("days", days);
        command.Parameters.Add(new NpgsqlParameter("target", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = happenedOn is { } day
                ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : DBNull.Value
        });
        command.Parameters.AddWithValue("limit", limit);

        var result = new List<ActivityEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ActivityEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return result;
    }

    /// <summary>
    /// Прогресът се смята от SQL, не от модела: за всяко (седмица, label,
    /// числово поле от data) — min/avg/max/брой. Моделът само разказва.
    /// </summary>
    public async Task<IReadOnlyList<ProgressRow>> GetProgressAsync(
        string userId,
        string kind,
        int days,
        IReadOnlyList<string>? labels,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT to_char(date_trunc('week', a.happened_at), 'YYYY-MM-DD') AS week,
                   a.label,
                   e.key,
                   min((e.value)::text::numeric),
                   round(avg((e.value)::text::numeric), 1),
                   max((e.value)::text::numeric),
                   count(*)
            FROM {_postgres.Schema}.activity_log a,
                 jsonb_each(a.data) e
            WHERE a.user_id = @user AND a.kind = @kind
              AND a.superseded_by IS NULL
              AND a.happened_at > now() - make_interval(days => @days)
              AND (@labels IS NULL OR lower(a.label) = ANY(@labels))
              AND jsonb_typeof(e.value) = 'number'
            GROUP BY 1, 2, 3
            ORDER BY 1, 2, 3;
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("days", days);
        command.Parameters.Add(new NpgsqlParameter(
            "labels",
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = labels is null
                ? DBNull.Value
                : labels.Select(label => label.ToLowerInvariant()).ToArray()
        });

        var result = new List<ProgressRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ProgressRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetInt64(6)));
        }

        return result;
    }

    public async Task<IReadOnlyList<PromptVersionInfo>> ListPromptVersionsAsync(
        string agentKey,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT p.id, p.version, p.instructions, p.note, p.created_at,
                   (p.id = a.active_prompt_version_id) AS active
            FROM {_postgres.Schema}.prompt_versions p
            JOIN {_postgres.Schema}.agents a ON a.key = p.agent_key
            WHERE p.agent_key = @key
            ORDER BY p.version DESC;
            """,
            connection);
        command.Parameters.AddWithValue("key", agentKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var versions = new List<PromptVersionInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(new PromptVersionInfo(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<DateTime>(4).ToUniversalTime(),
                reader.GetBoolean(5)));
        }

        return versions;
    }

    /// <summary>Нова версия (append-only): vN+1. По избор веднага става активна.</summary>
    public async Task<PromptVersionInfo> CreatePromptVersionAsync(
        string agentKey,
        string instructions,
        string? note,
        bool activate,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long id;
        int version;
        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {_postgres.Schema}.prompt_versions (agent_key, version, instructions, note)
                         SELECT @key, coalesce(max(version), 0) + 1, @instructions, @note
                         FROM {_postgres.Schema}.prompt_versions WHERE agent_key = @key
                         RETURNING id, version;
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("key", agentKey);
            insert.Parameters.AddWithValue("instructions", instructions);
            insert.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Агент '{agentKey}' не съществува.");
            }

            id = reader.GetInt64(0);
            version = reader.GetInt32(1);
        }

        if (activate)
        {
            await using var activateCommand = new NpgsqlCommand(
                $"UPDATE {_postgres.Schema}.agents SET active_prompt_version_id = @vid WHERE key = @key;",
                connection,
                transaction);
            activateCommand.Parameters.AddWithValue("vid", id);
            activateCommand.Parameters.AddWithValue("key", agentKey);
            await activateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PromptVersionInfo(id, version, instructions, note, DateTimeOffset.UtcNow, activate);
    }

    /// <summary>Активиране на съществуваща версия — самото „връщане назад“.</summary>
    public async Task<bool> ActivatePromptVersionAsync(
        string agentKey,
        int version,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_postgres.Schema}.agents a
            SET active_prompt_version_id = p.id
            FROM {_postgres.Schema}.prompt_versions p
            WHERE a.key = @key AND p.agent_key = @key AND p.version = @version;
            """,
            connection);
        command.Parameters.AddWithValue("key", agentKey);
        command.Parameters.AddWithValue("version", version);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> UpdateAgentAsync(
        string agentKey,
        string? model,
        bool? enabled,
        string? activation,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_postgres.Schema}.agents
            SET model = coalesce(@model, model),
                enabled = coalesce(@enabled, enabled),
                activation = coalesce(@activation, activation)
            WHERE key = @key;
            """,
            connection);
        command.Parameters.AddWithValue("key", agentKey);
        command.Parameters.AddWithValue("model", (object?)model ?? DBNull.Value);
        command.Parameters.AddWithValue("enabled", (object?)enabled ?? DBNull.Value);
        command.Parameters.AddWithValue("activation", (object?)activation ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Сесиите на потребителя за страницата История, с незадължителни филтри:
    /// текстово търсене В СЪДЪРЖАНИЕТО на репликите (не само в прегледа) и
    /// статус (active / compacted / closed).
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        string userId,
        string? searchText,
        string? status,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var statusFilter = status switch
        {
            "active" => "AND s.compacted_at IS NULL",
            "compacted" => "AND s.archive_document_id IS NOT NULL",
            "closed" => "AND s.compacted_at IS NOT NULL AND s.archive_document_id IS NULL",
            _ => string.Empty
        };
        var searchFilter = string.IsNullOrWhiteSpace(searchText)
            ? string.Empty
            : $"""
              AND EXISTS (SELECT 1 FROM {_postgres.Schema}.messages m
                          WHERE m.session_id = s.id AND m.content ILIKE @search)
              """;

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT s.id, s.started_at, s.compacted_at, s.archive_document_id,
                   (SELECT count(*) FROM {_postgres.Schema}.messages m WHERE m.session_id = s.id),
                   coalesce((SELECT left(m.content, 120) FROM {_postgres.Schema}.messages m
                             WHERE m.session_id = s.id AND m.role = 'user' ORDER BY m.id LIMIT 1), ''),
                   coalesce((SELECT sum(coalesce(m.input_tokens, 0) + coalesce(m.output_tokens, 0))
                             FROM {_postgres.Schema}.messages m WHERE m.session_id = s.id), 0)
            FROM {_postgres.Schema}.sessions s
            WHERE s.user_id = @user_id {statusFilter} {searchFilter}
            ORDER BY s.started_at DESC;
            """,
            connection);
        command.Parameters.AddWithValue("user_id", userId);
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            // ILIKE шаблонът е параметър — wildcards в потребителския текст
            // се неутрализират, за да търси буквално.
            var escaped = searchText.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            command.Parameters.AddWithValue("search", $"%{escaped}%");
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var sessions = new List<SessionSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new SessionSummary(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTime>(1).ToUniversalTime(),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2).ToUniversalTime(),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                checked((int)reader.GetInt64(4)),
                reader.GetString(5),
                checked((int)reader.GetInt64(6))));
        }

        return sessions;
    }

    /// <summary>Живите бройки за диаграмата „Как работи“ — по потребител.</summary>
    public async Task<HistoryStats> GetHistoryStatsAsync(string userId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
              (SELECT count(*) FROM {_postgres.Schema}.sessions WHERE user_id = @u),
              (SELECT count(*) FROM {_postgres.Schema}.messages WHERE user_id = @u),
              (SELECT count(*) FROM {_postgres.Schema}.documents WHERE user_id = @u AND document_type = 'chat'),
              (SELECT count(*) FROM {_postgres.Schema}.chunks WHERE user_id = @u),
              (SELECT coalesce(sum(coalesce(input_tokens, 0) + coalesce(output_tokens, 0)), 0)
               FROM {_postgres.Schema}.messages WHERE user_id = @u);
            """,
            connection);
        command.Parameters.AddWithValue("u", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new HistoryStats(
            checked((int)reader.GetInt64(0)),
            checked((int)reader.GetInt64(1)),
            checked((int)reader.GetInt64(2)),
            checked((int)reader.GetInt64(3)),
            reader.GetInt64(4));
    }

    /// <summary>Пълният суров разговор на една сесия, с произхода на всяка реплика.</summary>
    public async Task<IReadOnlyList<HistoryMessage>> GetSessionMessagesAsync(
        string userId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT role, content, created_at, agent_key, model, prompt_version,
                   context_refs::text, input_tokens, output_tokens, steps::text
            FROM {_postgres.Schema}.messages
            WHERE session_id = @session_id AND user_id = @user_id
            ORDER BY id;
            """,
            connection);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var messages = new List<HistoryMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new HistoryMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTime>(2).ToUniversalTime(),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return messages;
    }

    /// <summary>Текстът на chunk от паметта — за прегледа на context_refs в Историята.</summary>
    public async Task<string?> GetChunkContentAsync(
        string userId,
        string chunkId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT content FROM {_postgres.Schema}.chunks WHERE id = @id AND user_id = @user_id;",
            connection);
        command.Parameters.AddWithValue("id", chunkId);
        command.Parameters.AddWithValue("user_id", userId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>Активната (незатворена) сесия на потребителя, ако има такава.</summary>
    public async Task<ActiveSessionState?> LoadActiveSessionAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        Guid sessionId;
        string? seed;
        string? activeAgent;
        await using (var sessionCommand = new NpgsqlCommand(
                         $"""
                         SELECT id, summary_seed, active_agent FROM {_postgres.Schema}.sessions
                         WHERE user_id = @user_id AND compacted_at IS NULL
                         ORDER BY started_at DESC LIMIT 1;
                         """,
                         connection))
        {
            sessionCommand.Parameters.AddWithValue("user_id", userId);
            await using var sessionReader = await sessionCommand.ExecuteReaderAsync(cancellationToken);
            if (!await sessionReader.ReadAsync(cancellationToken))
            {
                return null;
            }

            sessionId = sessionReader.GetGuid(0);
            seed = sessionReader.IsDBNull(1) ? null : sessionReader.GetString(1);
            activeAgent = sessionReader.IsDBNull(2) ? null : sessionReader.GetString(2);
        }

        var messages = new List<ChatMessage>();
        var lastInputTokens = 0;
        await using (var messageCommand = new NpgsqlCommand(
                         $"""
                         SELECT role, content, created_at, input_tokens, agent_key,
                                model, prompt_version, output_tokens, steps::text,
                                context_input_tokens
                         FROM {_postgres.Schema}.messages
                         WHERE session_id = @session_id
                         ORDER BY id;
                         """,
                         connection))
        {
            messageCommand.Parameters.AddWithValue("session_id", sessionId);
            await using var reader = await messageCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                IReadOnlyList<string>? steps = null;
                if (!reader.IsDBNull(8))
                {
                    try
                    {
                        steps = System.Text.Json.JsonSerializer
                            .Deserialize<List<string>>(reader.GetString(8));
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // невалидни стъпки не бива да спрат възстановяването
                    }
                }

                messages.Add(new ChatMessage(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateTime>(2).ToUniversalTime(),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    steps));
                if (!reader.IsDBNull(9))
                {
                    lastInputTokens = reader.GetInt32(9);
                }
                else if (!reader.IsDBNull(3))
                {
                    lastInputTokens = reader.GetInt32(3);
                }
            }
        }

        return new ActiveSessionState(sessionId, seed, messages, lastInputTokens, activeAgent);
    }

    public async Task<Guid> CreateSessionAsync(
        string userId,
        string? summarySeed,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var sessionId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_postgres.Schema}.sessions (id, user_id, summary_seed)
            VALUES (@id, @user_id, @seed);
            """,
            connection);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("seed", (object?)summarySeed ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return sessionId;
    }

    /// <summary>Записва една завършена размяна: репликата на потребителя + отговора с произхода му.</summary>
    public async Task AppendTurnAsync(
        Guid sessionId,
        string userId,
        string userText,
        IReadOnlyList<string> notes,
        IReadOnlyList<string> steps,
        string assistantText,
        AgentRuntime agent,
        string? contextRefsJson,
        TokenUsage usage,
        int contextInputTokens,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var userCommand = new NpgsqlCommand(
                         $"""
                         INSERT INTO {_postgres.Schema}.messages (session_id, user_id, role, content)
                         VALUES (@session_id, @user_id, 'user', @content);
                         """,
                         connection,
                         transaction))
        {
            userCommand.Parameters.AddWithValue("session_id", sessionId);
            userCommand.Parameters.AddWithValue("user_id", userId);
            userCommand.Parameters.AddWithValue("content", userText);
            await userCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Системните бележки (handoff/finish) — между репликата и отговора,
        // за да е редът в базата същият като в живия разговор.
        foreach (var note in notes)
        {
            await using var noteCommand = new NpgsqlCommand(
                $"""
                INSERT INTO {_postgres.Schema}.messages (session_id, user_id, role, content)
                VALUES (@session_id, @user_id, 'system', @content);
                """,
                connection,
                transaction);
            noteCommand.Parameters.AddWithValue("session_id", sessionId);
            noteCommand.Parameters.AddWithValue("user_id", userId);
            noteCommand.Parameters.AddWithValue("content", note);
            await noteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var assistantCommand = new NpgsqlCommand(
                         $"""
                         INSERT INTO {_postgres.Schema}.messages
                             (session_id, user_id, role, content, agent_key, model, prompt_version,
                              context_refs, steps, input_tokens, context_input_tokens, output_tokens)
                         VALUES
                             (@session_id, @user_id, 'assistant', @content, @agent_key, @model, @prompt_version,
                              CAST(@context_refs AS jsonb), CAST(@steps AS jsonb), @input_tokens,
                              @context_input_tokens, @output_tokens);
                         """,
                         connection,
                         transaction))
        {
            assistantCommand.Parameters.AddWithValue("session_id", sessionId);
            assistantCommand.Parameters.AddWithValue("user_id", userId);
            assistantCommand.Parameters.AddWithValue("content", assistantText);
            assistantCommand.Parameters.AddWithValue("agent_key", agent.Key);
            assistantCommand.Parameters.AddWithValue("model", agent.Model);
            assistantCommand.Parameters.AddWithValue("prompt_version", agent.PromptVersion);
            assistantCommand.Parameters.AddWithValue("context_refs", (object?)contextRefsJson ?? DBNull.Value);
            assistantCommand.Parameters.AddWithValue("steps", steps.Count == 0
                ? DBNull.Value
                : System.Text.Json.JsonSerializer.Serialize(steps));
            assistantCommand.Parameters.AddWithValue("input_tokens", usage.InputTokens);
            assistantCommand.Parameters.AddWithValue("context_input_tokens", contextInputTokens);
            assistantCommand.Parameters.AddWithValue("output_tokens", usage.OutputTokens);
            await assistantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Системна бележка в разговора: handoff, приключване на задача.</summary>
    public async Task AppendNoteAsync(
        Guid sessionId,
        string userId,
        string text,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_postgres.Schema}.messages (session_id, user_id, role, content)
            VALUES (@session_id, @user_id, 'system', @content);
            """,
            connection);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("content", text);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Затваря сесия — при компактиране сочи и към архивното резюме.</summary>
    public async Task CloseSessionAsync(
        Guid sessionId,
        string? archiveDocumentId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_postgres.Schema}.sessions
            SET compacted_at = now(), archive_document_id = @archive_id
            WHERE id = @id;
            """,
            connection);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("archive_id", (object?)archiveDocumentId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            // Същите таблици като лабораторията (през PostgresSearchStore),
            // плюс колоните, които отличават чат данните. Съществуващите
            // редове (отпреди мулти-потребителския режим) стават на 'ivan'.
            await _store.InitializeAsync(_labSettings.OpenAI.EmbeddingDimensions, cancellationToken);

            await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var alter = new NpgsqlCommand(
                $"""
                ALTER TABLE {_postgres.Schema}.documents
                    ADD COLUMN IF NOT EXISTS document_type text,
                    ADD COLUMN IF NOT EXISTS created_at timestamptz,
                    ADD COLUMN IF NOT EXISTS user_id text NOT NULL DEFAULT 'ivan';
                ALTER TABLE {_postgres.Schema}.chunks
                    ADD COLUMN IF NOT EXISTS user_id text NOT NULL DEFAULT 'ivan';
                CREATE INDEX IF NOT EXISTS ix_chat_documents_user ON {_postgres.Schema}.documents (user_id);
                CREATE INDEX IF NOT EXISTS ix_chat_chunks_user ON {_postgres.Schema}.chunks (user_id);

                -- Суровият слой: сесии (разговори) и техните реплики.
                CREATE TABLE IF NOT EXISTS {_postgres.Schema}.sessions (
                    id uuid PRIMARY KEY,
                    user_id text NOT NULL,
                    started_at timestamptz NOT NULL DEFAULT now(),
                    compacted_at timestamptz NULL,
                    archive_document_id text NULL REFERENCES {_postgres.Schema}.documents(id),
                    summary_seed text NULL
                );
                CREATE INDEX IF NOT EXISTS ix_chat_sessions_user ON {_postgres.Schema}.sessions (user_id);

                CREATE TABLE IF NOT EXISTS {_postgres.Schema}.messages (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    session_id uuid NOT NULL REFERENCES {_postgres.Schema}.sessions(id) ON DELETE CASCADE,
                    user_id text NOT NULL,
                    role text NOT NULL,
                    content text NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    agent_key text NULL,
                    model text NULL,
                    prompt_version int NULL,
                    context_refs jsonb NULL,
                    input_tokens int NULL,
                    context_input_tokens int NULL,
                    output_tokens int NULL
                );
                CREATE INDEX IF NOT EXISTS ix_chat_messages_session ON {_postgres.Schema}.messages (session_id);

                -- Агенти и версионирани промптове. active_prompt_version_id е
                -- мек показалец (кодът го поддържа); версиите са append-only.
                CREATE TABLE IF NOT EXISTS {_postgres.Schema}.agents (
                    key text PRIMARY KEY,
                    display_name text NOT NULL,
                    kind text NOT NULL,
                    enabled boolean NOT NULL DEFAULT true,
                    provider text NOT NULL,
                    model text NOT NULL,
                    parameters jsonb NULL,
                    active_prompt_version_id bigint NULL
                );

                CREATE TABLE IF NOT EXISTS {_postgres.Schema}.prompt_versions (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    agent_key text NOT NULL REFERENCES {_postgres.Schema}.agents(key),
                    version int NOT NULL,
                    instructions text NOT NULL,
                    note text NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    UNIQUE (agent_key, version)
                );

                -- Регистърът на специалистите: activation е „какво поемам" —
                -- визитката, по която оркестраторът избира. transport е шевът
                -- за бъдещи външни (A2A) агенти; днес всичко е 'local'.
                ALTER TABLE {_postgres.Schema}.agents
                    ADD COLUMN IF NOT EXISTS activation text NOT NULL DEFAULT '',
                    ADD COLUMN IF NOT EXISTS transport text NOT NULL DEFAULT 'local';
                ALTER TABLE {_postgres.Schema}.sessions
                    ADD COLUMN IF NOT EXISTS active_agent text NULL;

                -- Видимите стъпки на хода (инструментите: регистър, handoff,
                -- запис, дневник...) — одитната следа какво е правил агентът.
                ALTER TABLE {_postgres.Schema}.messages
                    ADD COLUMN IF NOT EXISTS steps jsonb NULL,
                    ADD COLUMN IF NOT EXISTS context_input_tokens int NULL;

                -- Кой външен канал (telegram, ...) към кой наш потребител сочи.
                -- Свързването е изрично (изборът при /start); изтриването на
                -- реда прекъсва достъпа от този канал.
                CREATE TABLE IF NOT EXISTS {_postgres.Schema}.channel_links (
                    channel text NOT NULL,
                    external_id text NOT NULL,
                    user_id text NOT NULL,
                    linked_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (channel, external_id)
                );

                -- Артефакти: готовите резултати (програма, режим, рутина) са
                -- документи (document_type='artifact', версия = нов документ);
                -- тук е само стрелката „текущото от този вид". Старите версии
                -- остават — стрелката само се мести.
                CREATE TABLE IF NOT EXISTS {_postgres.Schema}.user_artifacts (
                    user_id text NOT NULL,
                    kind text NOT NULL,
                    document_id text NOT NULL REFERENCES {_postgres.Schema}.documents(id),
                    updated_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (user_id, kind)
                );

                -- Бележки около артефакт (append-only). Закачени за вида, не
                -- за версията — бележката живее с програмата, не с файла, и е
                -- суровината за следващата ревизия.
                CREATE TABLE IF NOT EXISTS {_postgres.Schema}.artifact_notes (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    user_id text NOT NULL,
                    kind text NOT NULL,
                    note text NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now()
                );

                -- Дневникът: структурирани записи за статистика. label = какво
                -- (клек, овесена каша), data = числата (jsonb). Корекция = нов
                -- ред + superseded_by на стария; статистиката чете само
                -- действащите. Нищо не се трие.
                CREATE TABLE IF NOT EXISTS {_postgres.Schema}.activity_log (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    user_id text NOT NULL,
                    kind text NOT NULL,
                    label text NOT NULL,
                    data jsonb NULL,
                    note text NULL,
                    happened_at timestamptz NOT NULL DEFAULT now(),
                    superseded_by bigint NULL
                );
                CREATE INDEX IF NOT EXISTS ix_chat_activity_user
                    ON {_postgres.Schema}.activity_log (user_id, happened_at);

                -- Дребно състояние на процеса, което трябва да преживява
                -- рестарт (напр. telegram_offset — потвърденият update_id).
                CREATE TABLE IF NOT EXISTS {_postgres.Schema}.app_state (
                    key text PRIMARY KEY,
                    value text NOT NULL,
                    updated_at timestamptz NOT NULL DEFAULT now()
                );
                """,
                connection);
            await alter.ExecuteNonQueryAsync(cancellationToken);
            await SeedAgentAsync(connection, "chat", "Чат асистент", "chat", DefaultPrompts.Chat, null, cancellationToken);
            await SeedAgentAsync(connection, "compactor", "Компактор", "system", DefaultPrompts.Compactor, null, cancellationToken);
            await SeedAgentAsync(
                connection,
                "coach",
                "Фитнес треньор",
                "specialist",
                DefaultPrompts.Coach,
                "Фитнес тренировки, упражнения, програми, възстановяване и хранителен режим.",
                cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }
}
