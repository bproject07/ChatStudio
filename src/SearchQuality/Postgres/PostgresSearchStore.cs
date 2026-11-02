using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using SearchQuality.Domain;

namespace SearchQuality.Postgres;

public sealed partial class PostgresSearchStore
{
    private readonly string _connectionString;
    private readonly string _schema;

    public PostgresSearchStore(PostgresSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "PostgreSQL is not configured. Set SEARCHQUALITY_POSTGRES_CONNECTION_STRING.");
        }

        if (!SchemaNameRegex().IsMatch(settings.Schema))
        {
            throw new ArgumentException("PostgreSQL schema name contains unsupported characters.", nameof(settings));
        }

        _connectionString = settings.ConnectionString;
        _schema = settings.Schema;
    }

    public async Task<PostgresHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector');",
                connection);
            var installed = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
            return new PostgresHealth(true, installed, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PostgresHealth(false, false, exception.Message);
        }
    }

    public async Task<PostgresIndexStats> GetIndexStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"SELECT (SELECT count(*) FROM {_schema}.documents), " +
                $"(SELECT count(*) FROM {_schema}.chunks);",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new PostgresIndexStats(false, 0, 0, "Index statistics returned no rows.");
            }

            return new PostgresIndexStats(
                true,
                checked((int)reader.GetInt64(0)),
                checked((int)reader.GetInt64(1)),
                null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PostgresIndexStats(false, 0, 0, exception.Message);
        }
    }

    public async Task InitializeAsync(int embeddingDimensions, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(embeddingDimensions, 1);

        var sql = $"""
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE SCHEMA IF NOT EXISTS {_schema};

            CREATE TABLE IF NOT EXISTS {_schema}.documents (
                id text PRIMARY KEY,
                title text NOT NULL,
                file_name text NOT NULL,
                content text NOT NULL,
                content_hash text NOT NULL DEFAULT ''
            );

            ALTER TABLE {_schema}.documents
                ADD COLUMN IF NOT EXISTS content_hash text NOT NULL DEFAULT '';

            CREATE TABLE IF NOT EXISTS {_schema}.chunks (
                id text PRIMARY KEY,
                document_id text NOT NULL REFERENCES {_schema}.documents(id) ON DELETE CASCADE,
                document_title text NOT NULL,
                ordinal integer NOT NULL,
                content text NOT NULL,
                embedding vector({embeddingDimensions}),
                search_vector tsvector GENERATED ALWAYS AS (
                    to_tsvector('simple', coalesce(document_title, '') || ' ' || coalesce(content, ''))
                ) STORED
            );

            CREATE INDEX IF NOT EXISTS ix_chunks_search_vector
                ON {_schema}.chunks USING gin (search_vector);
            CREATE INDEX IF NOT EXISTS ix_chunks_embedding_hnsw
                ON {_schema}.chunks USING hnsw (embedding vector_cosine_ops);
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceCorpusAsync(
        IReadOnlyList<SourceDocument> documents,
        IReadOnlyDictionary<string, string> contentHashes,
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<float[]> embeddings,
        CancellationToken cancellationToken)
    {
        if (chunks.Count != embeddings.Count)
        {
            throw new ArgumentException("Every chunk must have exactly one embedding.", nameof(embeddings));
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = new NpgsqlCommand(
                         $"DELETE FROM {_schema}.chunks; DELETE FROM {_schema}.documents;",
                         connection,
                         transaction))
        {
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertDocumentsAsync(connection, transaction, documents, contentHashes, cancellationToken);
        await InsertChunksAsync(connection, transaction, chunks, embeddings, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Инкрементално обновяване: изтрива посочените документи (изтритите от
    /// корпуса и старите версии на променените — chunks падат каскадно) и
    /// вмъква новите/променените с частите им. Непроменените не се докосват.
    /// </summary>
    public async Task SyncCorpusAsync(
        IReadOnlyList<SourceDocument> documentsToIndex,
        IReadOnlyDictionary<string, string> contentHashes,
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<float[]> embeddings,
        IReadOnlyList<string> removedDocumentIds,
        CancellationToken cancellationToken)
    {
        if (chunks.Count != embeddings.Count)
        {
            throw new ArgumentException("Every chunk must have exactly one embedding.", nameof(embeddings));
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var idsToDelete = removedDocumentIds
            .Concat(documentsToIndex.Select(document => document.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (idsToDelete.Length > 0)
        {
            await using var delete = new NpgsqlCommand(
                $"DELETE FROM {_schema}.documents WHERE id = ANY(@ids);",
                connection,
                transaction);
            delete.Parameters.AddWithValue("ids", idsToDelete);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertDocumentsAsync(connection, transaction, documentsToIndex, contentHashes, cancellationToken);
        await InsertChunksAsync(connection, transaction, chunks, embeddings, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDocumentHashesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT id, content_hash FROM {_schema}.documents;",
            connection);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hashes[reader.GetString(0)] = reader.GetString(1);
        }

        return hashes;
    }

    private async Task InsertDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<SourceDocument> documents,
        IReadOnlyDictionary<string, string> contentHashes,
        CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            await using var insertDocument = new NpgsqlCommand(
                $"""
                INSERT INTO {_schema}.documents (id, title, file_name, content, content_hash)
                VALUES (@id, @title, @file_name, @content, @content_hash);
                """,
                connection,
                transaction);
            insertDocument.Parameters.AddWithValue("id", document.Id);
            insertDocument.Parameters.AddWithValue("title", document.Title);
            insertDocument.Parameters.AddWithValue("file_name", document.FileName);
            insertDocument.Parameters.AddWithValue("content", document.Content);
            insertDocument.Parameters.AddWithValue(
                "content_hash",
                contentHashes.TryGetValue(document.Id, out var hash) ? hash : string.Empty);
            await insertDocument.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task InsertChunksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<float[]> embeddings,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            await using var insertChunk = new NpgsqlCommand(
                $"""
                INSERT INTO {_schema}.chunks
                    (id, document_id, document_title, ordinal, content, embedding)
                VALUES
                    (@id, @document_id, @document_title, @ordinal, @content, CAST(@embedding AS vector));
                """,
                connection,
                transaction);
            insertChunk.Parameters.AddWithValue("id", chunk.Id);
            insertChunk.Parameters.AddWithValue("document_id", chunk.DocumentId);
            insertChunk.Parameters.AddWithValue("document_title", chunk.DocumentTitle);
            insertChunk.Parameters.AddWithValue("ordinal", chunk.Ordinal);
            insertChunk.Parameters.AddWithValue("content", chunk.Text);
            insertChunk.Parameters.AddWithValue("embedding", PgVectorLiteral.Format(embeddings[index]));
            await insertChunk.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public Task<IReadOnlyList<SearchHit>> SearchFullTextAsync(
        string query,
        int topK,
        CancellationToken cancellationToken)
    {
        var lexicalQuery = LexicalQueryBuilder.Build(query);
        if (string.IsNullOrWhiteSpace(lexicalQuery))
        {
            return Task.FromResult<IReadOnlyList<SearchHit>>([]);
        }

        const string scoreExpression = "ts_rank_cd(search_vector, to_tsquery('simple', @query))";
        var sql = $"""
            SELECT document_id, document_title, id, content,
                   ({scoreExpression})::double precision AS score
            FROM {_schema}.chunks
            WHERE search_vector @@ to_tsquery('simple', @query)
            ORDER BY score DESC, document_id, ordinal
            LIMIT @top_k;
            """;

        return SearchAsync(sql, lexicalQuery, null, topK, cancellationToken);
    }

    public Task<IReadOnlyList<SearchHit>> SearchVectorAsync(
        string query,
        IReadOnlyList<float> embedding,
        int topK,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT document_id, document_title, id, content,
                   (1 - (embedding <=> CAST(@embedding AS vector)))::double precision AS score
            FROM {_schema}.chunks
            WHERE embedding IS NOT NULL
            ORDER BY embedding <=> CAST(@embedding AS vector), document_id, ordinal
            LIMIT @top_k;
            """;

        return SearchAsync(sql, query, PgVectorLiteral.Format(embedding), topK, cancellationToken);
    }

    private async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string sql,
        string query,
        string? embedding,
        int topK,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("top_k", topK);
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

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNameRegex();
}
