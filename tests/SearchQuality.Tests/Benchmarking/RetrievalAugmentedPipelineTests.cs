using SearchQuality.Abstractions;
using SearchQuality.Benchmarking;
using SearchQuality.Domain;

namespace SearchQuality.Tests.Benchmarking;

public sealed class RetrievalAugmentedPipelineTests
{
    [Fact]
    public async Task Execute_keeps_gpt_and_embedding_usage_separate_and_combines_the_total()
    {
        var hit = new SearchHit("doc", "Document", "chunk", "Context", 0.9);
        var pipeline = new RetrievalAugmentedPipeline(
            new StubRetriever(
                new RetrievalResult([hit], new TokenUsage(4, 0, 4))),
            new StubAnswerGenerator(
                new GeneratedAnswer("Answer", new TokenUsage(10, 2, 12))));

        var result = await pipeline.ExecuteAsync(
            "Question",
            5,
            TestContext.Current.CancellationToken);

        Assert.Equal(12, result.Answer.Usage.TotalTokens);
        Assert.Equal(4, result.EmbeddingUsage.TotalTokens);
        Assert.Equal(14, result.TotalUsage.InputTokens);
        Assert.Equal(2, result.TotalUsage.OutputTokens);
        Assert.Equal(16, result.TotalUsage.TotalTokens);
    }

    [Fact]
    public async Task Retrieve_stage_does_not_call_gpt_and_generation_receives_only_supplied_hits()
    {
        var first = new SearchHit("first", "First", "first", "First context", 0.9);
        var second = new SearchHit("second", "Second", "second", "Second context", 0.8);
        var generator = new TrackingAnswerGenerator();
        var pipeline = new RetrievalAugmentedPipeline(
            new StubRetriever(new RetrievalResult([first, second], TokenUsage.Empty)),
            generator);

        var retrieved = await pipeline.RetrieveAsync(
            "Question",
            5,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, generator.CallCount);

        await pipeline.GenerateAsync(
            "Question",
            retrieved,
            [second],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, generator.CallCount);
        Assert.Equal(["second"], generator.LastContext.Select(hit => hit.DocumentId));
    }

    private sealed class StubRetriever(RetrievalResult result) : IRetriever
    {
        public string Name => "stub";

        public Task<RetrievalResult> SearchAsync(
            string query,
            int topK,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class StubAnswerGenerator(GeneratedAnswer answer) : IAnswerGenerator
    {
        public Task<GeneratedAnswer> GenerateAsync(
            string question,
            IReadOnlyList<SearchHit> context,
            CancellationToken cancellationToken) =>
            Task.FromResult(answer);
    }

    private sealed class TrackingAnswerGenerator : IAnswerGenerator
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<SearchHit> LastContext { get; private set; } = [];

        public Task<GeneratedAnswer> GenerateAsync(
            string question,
            IReadOnlyList<SearchHit> context,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastContext = context;
            return Task.FromResult(new GeneratedAnswer("Answer", TokenUsage.Empty));
        }
    }
}
