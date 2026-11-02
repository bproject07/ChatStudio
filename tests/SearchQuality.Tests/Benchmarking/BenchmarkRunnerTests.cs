using SearchQuality.Abstractions;
using SearchQuality.Benchmarking;
using SearchQuality.Domain;

namespace SearchQuality.Tests.Benchmarking;

public sealed class BenchmarkRunnerTests
{
    [Fact]
    public async Task Run_aggregates_success_and_failure_per_strategy()
    {
        var evaluations = new[]
        {
            new EvaluationCase("q1", "Question", ["doc"], "Answer", ["fact"])
        };
        ISearchPipeline[] pipelines =
        [
            new StubPipeline("good", new PipelineResult(
                "good",
                [new SearchHit("doc", "Doc", "chunk", "fact", 1)],
                new GeneratedAnswer("fact", new TokenUsage(10, 2, 12)),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20))
            {
                EmbeddingUsage = new TokenUsage(3, 0, 3)
            }),
            new ThrowingPipeline()
        ];

        var report = await new BenchmarkRunner(pipelines, 1)
            .RunAsync(evaluations, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Strategies.Count);
        var good = Assert.Single(report.Strategies, summary => summary.Strategy == "good");
        Assert.Equal(1, good.SuccessfulCases);
        Assert.Equal(1, good.HitRateAtK);
        Assert.Equal(12, good.TotalGptTokens);
        Assert.Equal(3, good.TotalEmbeddingTokens);
        Assert.Equal(15, good.TotalTokens);
        var failed = Assert.Single(report.Strategies, summary => summary.Strategy == "bad");
        Assert.Equal(1, failed.FailedCases);
    }

    [Fact]
    public async Task Run_reports_retrieval_only_latency_separately_from_total()
    {
        var evaluations = new[]
        {
            new EvaluationCase("q1", "Question", ["doc"], "Answer", ["fact"])
        };
        ISearchPipeline[] pipelines =
        [
            new StubPipeline("timed", new PipelineResult(
                "timed",
                [new SearchHit("doc", "Doc", "chunk", "fact", 1)],
                new GeneratedAnswer("fact", TokenUsage.Empty),
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(160)))
        ];

        var report = await new BenchmarkRunner(pipelines, 1)
            .RunAsync(evaluations, cancellationToken: TestContext.Current.CancellationToken);

        var summary = Assert.Single(report.Strategies);
        Assert.Equal(40, summary.MeanRetrievalMilliseconds, precision: 6);
        Assert.Equal(200, summary.MeanLatencyMilliseconds, precision: 6);
    }

    private sealed class StubPipeline(string name, PipelineResult result) : ISearchPipeline
    {
        public string Name => name;

        public Task<PipelineResult> ExecuteAsync(string question, int topK, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingPipeline : ISearchPipeline
    {
        public string Name => "bad";

        public Task<PipelineResult> ExecuteAsync(string question, int topK, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("expected failure");
    }
}
