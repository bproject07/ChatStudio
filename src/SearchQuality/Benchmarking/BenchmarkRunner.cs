using SearchQuality.Abstractions;
using SearchQuality.Domain;

namespace SearchQuality.Benchmarking;

public sealed class BenchmarkRunner(IReadOnlyList<ISearchPipeline> pipelines, int topK)
{
    public async Task<BenchmarkReport> RunAsync(
        IReadOnlyList<EvaluationCase> evaluations,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var caseResults = new List<BenchmarkCaseResult>();

        foreach (var evaluation in evaluations)
        {
            foreach (var pipeline in pipelines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke($"{pipeline.Name}: {evaluation.Id}");

                PipelineResult result;
                try
                {
                    result = await pipeline.ExecuteAsync(
                        evaluation.Question,
                        topK,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result = PipelineResult.Failed(pipeline.Name, exception);
                }

                caseResults.Add(new BenchmarkCaseResult(
                    evaluation,
                    result,
                    SearchMetrics.Calculate(evaluation, result, topK)));
            }
        }

        var summaries = caseResults
            .GroupBy(item => item.Result.Strategy, StringComparer.OrdinalIgnoreCase)
            .Select(CreateSummary)
            .OrderByDescending(summary => summary.MeanReciprocalRank)
            .ThenByDescending(summary => summary.MeanRequiredTermCoverage)
            .ToArray();

        return new BenchmarkReport(DateTimeOffset.UtcNow, topK, caseResults, summaries)
        {
            MetricsVersion = SearchMetrics.CurrentMetricsVersion
        };
    }

    private static StrategySummary CreateSummary(IGrouping<string, BenchmarkCaseResult> group)
    {
        var all = group.ToArray();
        var successful = all.Where(item => item.Result.Error is null).ToArray();

        return new StrategySummary(
            group.Key,
            successful.Length,
            all.Length - successful.Length,
            Average(successful, item => item.Metrics.HitAtK ? 1 : 0),
            Average(successful, item => item.Metrics.PrecisionAtK),
            Average(successful, item => item.Metrics.RecallAtK),
            Average(successful, item => item.Metrics.MeanReciprocalRank),
            Average(successful, item => item.Metrics.RequiredTermCoverage),
            Average(successful, item => item.Result.TotalDuration.TotalMilliseconds),
            successful.Sum(item => item.Result.Answer.Usage.InputTokens),
            successful.Sum(item => item.Result.Answer.Usage.OutputTokens))
        {
            MeanRetrievalMilliseconds = Average(
                successful,
                item => item.Result.RetrievalDuration.TotalMilliseconds),
            TotalEmbeddingTokens = successful.Sum(item => item.Result.EmbeddingUsage.TotalTokens)
        };
    }

    private static double Average(
        IReadOnlyCollection<BenchmarkCaseResult> items,
        Func<BenchmarkCaseResult, double> selector) =>
        items.Count == 0 ? 0 : items.Average(selector);
}
