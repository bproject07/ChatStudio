using SearchQuality.Domain;

namespace SearchQuality.Abstractions;

public interface IDocumentChunker
{
    IReadOnlyList<DocumentChunk> Chunk(SourceDocument document);
}

public interface IEmbeddingService
{
    Task<EmbeddingResult> CreateEmbeddingsAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken);
}

public interface IRetriever
{
    string Name { get; }

    Task<RetrievalResult> SearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken);
}

public interface IAnswerGenerator
{
    Task<GeneratedAnswer> GenerateAsync(
        string question,
        IReadOnlyList<SearchHit> context,
        CancellationToken cancellationToken);
}

public interface ISearchPipeline
{
    string Name { get; }

    Task<PipelineResult> ExecuteAsync(
        string question,
        int topK,
        CancellationToken cancellationToken);
}
