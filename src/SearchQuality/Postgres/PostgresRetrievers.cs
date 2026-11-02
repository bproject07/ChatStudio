using SearchQuality.Abstractions;
using SearchQuality.Domain;

namespace SearchQuality.Postgres;

public sealed class PostgresFullTextRetriever(PostgresSearchStore store) : IRetriever
{
    public string Name => "postgres-full-text";

    public async Task<RetrievalResult> SearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken)
    {
        var hits = await store.SearchFullTextAsync(query, topK, cancellationToken);
        return new RetrievalResult(hits, TokenUsage.Empty);
    }
}

public sealed class PostgresVectorRetriever(
    PostgresSearchStore store,
    IEmbeddingService embeddingService) : IRetriever
{
    public string Name => "postgres-pgvector";

    public async Task<RetrievalResult> SearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken)
    {
        var embedding = await embeddingService.CreateEmbeddingsAsync([query], cancellationToken);
        var hits = await store.SearchVectorAsync(query, embedding.Vectors[0], topK, cancellationToken);
        return new RetrievalResult(hits, embedding.Usage);
    }
}

public sealed class PostgresHybridRetriever(
    PostgresSearchStore store,
    IEmbeddingService embeddingService) : IRetriever
{
    private const int MinimumCandidateCount = 20;
    private const int CandidateMultiplier = 4;
    private const int MaximumCandidateCount = 100;

    public string Name => "postgres-hybrid";

    public async Task<RetrievalResult> SearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken)
    {
        var candidateCount = Math.Min(
            MaximumCandidateCount,
            Math.Max(MinimumCandidateCount, topK * CandidateMultiplier));
        var lexicalTask = store.SearchFullTextAsync(query, candidateCount, cancellationToken);
        var embeddingTask = embeddingService.CreateEmbeddingsAsync([query], cancellationToken);

        await Task.WhenAll(lexicalTask, embeddingTask);
        var lexicalHits = await lexicalTask;
        var embedding = await embeddingTask;
        var vectorHits = await store.SearchVectorAsync(
            query,
            embedding.Vectors[0],
            candidateCount,
            cancellationToken);
        var fused = ReciprocalRankFusion.Fuse(lexicalHits, vectorHits, topK);

        return new RetrievalResult(fused, embedding.Usage);
    }
}
