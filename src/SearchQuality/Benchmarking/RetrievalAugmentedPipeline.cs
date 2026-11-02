using System.Diagnostics;
using SearchQuality.Abstractions;
using SearchQuality.Domain;

namespace SearchQuality.Benchmarking;

public sealed record RetrievedContext(
    RetrievalResult Retrieval,
    TimeSpan Duration);

public sealed class RetrievalAugmentedPipeline(
    IRetriever retriever,
    IAnswerGenerator answerGenerator) : ISearchPipeline
{
    public string Name => retriever.Name;

    public async Task<PipelineResult> ExecuteAsync(
        string question,
        int topK,
        CancellationToken cancellationToken)
    {
        var context = await RetrieveAsync(question, topK, cancellationToken);
        return await GenerateAsync(question, context, context.Retrieval.Hits, cancellationToken);
    }

    public async Task<RetrievedContext> RetrieveAsync(
        string question,
        int topK,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var retrieval = await retriever.SearchAsync(question, topK, cancellationToken);
        timer.Stop();

        return new RetrievedContext(retrieval, timer.Elapsed);
    }

    public async Task<PipelineResult> GenerateAsync(
        string question,
        RetrievedContext context,
        IReadOnlyList<SearchHit> hits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(hits);

        var timer = Stopwatch.StartNew();
        var answer = await answerGenerator.GenerateAsync(question, hits, cancellationToken);
        timer.Stop();

        return new PipelineResult(
            Name,
            hits,
            answer,
            context.Duration,
            timer.Elapsed)
        {
            EmbeddingUsage = context.Retrieval.EmbeddingUsage
        };
    }
}
