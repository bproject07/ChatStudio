using SearchQuality.Benchmarking;
using SearchQuality.Domain;

namespace SearchQuality.Tests.Benchmarking;

public sealed class SearchMetricsTests
{
    [Fact]
    public void Calculate_scores_retrieval_rank_and_answer_terms()
    {
        var evaluation = new EvaluationCase(
            "q1",
            "question",
            ["expected", "also-expected"],
            "reference",
            ["15 минути", "P1"]);
        var result = new PipelineResult(
            "test",
            [
                new SearchHit("wrong", "Wrong", "1", "x", 0.9),
                new SearchHit("expected", "Expected", "2", "y", 0.8)
            ],
            new GeneratedAnswer("При P1 срокът е 15 минути.", TokenUsage.Empty),
            TimeSpan.Zero,
            TimeSpan.Zero);

        var metrics = SearchMetrics.Calculate(evaluation, result, 5);

        Assert.True(metrics.HitAtK);
        // 2 различни върнати документа, 1 от тях очакван.
        Assert.Equal(0.5, metrics.PrecisionAtK, 6);
        Assert.Equal(0.5, metrics.RecallAtK, 6);
        Assert.Equal(0.5, metrics.MeanReciprocalRank, 6);
        Assert.Equal(1, metrics.RequiredTermCoverage, 6);
    }

    [Fact]
    public void Precision_counts_distinct_documents_so_duplicate_chunks_do_not_inflate_it()
    {
        var evaluation = new EvaluationCase("q1", "question", ["expected"], "reference", []);
        var result = new PipelineResult(
            "test",
            [
                new SearchHit("expected", "Expected", "1", "a", 0.9),
                new SearchHit("expected", "Expected", "2", "b", 0.8),
                new SearchHit("expected", "Expected", "3", "c", 0.7),
                new SearchHit("wrong", "Wrong", "4", "d", 0.6)
            ],
            new GeneratedAnswer(string.Empty, TokenUsage.Empty),
            TimeSpan.Zero,
            TimeSpan.Zero);

        var metrics = SearchMetrics.Calculate(evaluation, result, 5);

        // 2 различни документа, 1 очакван: трите chunk-а от „expected“ не броят тройно.
        Assert.Equal(0.5, metrics.PrecisionAtK, 6);
        Assert.Equal(1, metrics.RecallAtK, 6);
    }

    [Fact]
    public void Precision_is_not_penalized_when_fewer_than_k_documents_are_returned()
    {
        var evaluation = new EvaluationCase("q1", "question", ["expected"], "reference", []);
        var result = new PipelineResult(
            "test",
            [new SearchHit("expected", "Expected", "1", "a", 0.9)],
            new GeneratedAnswer(string.Empty, TokenUsage.Empty),
            TimeSpan.Zero,
            TimeSpan.Zero);

        var metrics = SearchMetrics.Calculate(evaluation, result, 5);

        // Единственият върнат документ е правилен: 1/1, а не 1/5.
        Assert.Equal(1, metrics.PrecisionAtK, 6);
    }

    [Fact]
    public void Precision_is_zero_when_nothing_is_returned()
    {
        var evaluation = new EvaluationCase("q1", "question", ["expected"], "reference", []);
        var result = new PipelineResult(
            "test",
            [],
            new GeneratedAnswer(string.Empty, TokenUsage.Empty),
            TimeSpan.Zero,
            TimeSpan.Zero);

        var metrics = SearchMetrics.Calculate(evaluation, result, 5);

        Assert.False(metrics.HitAtK);
        Assert.Equal(0, metrics.PrecisionAtK, 6);
    }

    [Theory]
    [InlineData("Първият отговор е до **4 работни часа** за P1.", "до 4 работни часа")]
    [InlineData("Лицензът включва точно 3 касови места.", "три касови места")]
    [InlineData("Срокът е 45 дена от заявката.", "45 дни")]
    [InlineData("Включва   до\n24 часа автономна работа.", "до 24 часа")]
    public void Term_coverage_normalizes_markdown_numerals_and_word_forms(string answer, string term)
    {
        var evaluation = new EvaluationCase("q1", "question", ["doc"], "reference", [term]);
        var result = new PipelineResult(
            "test",
            [new SearchHit("doc", "Doc", "1", "text", 0.9)],
            new GeneratedAnswer(answer, TokenUsage.Empty),
            TimeSpan.Zero,
            TimeSpan.Zero);

        var metrics = SearchMetrics.Calculate(evaluation, result, 5);

        Assert.Equal(1, metrics.RequiredTermCoverage, 6);
    }

    [Theory]
    [InlineData("Срещата е в петък сутринта.", "5")]
    [InlineData("И петте каси работят едновременно.", "5")]
    [InlineData("Отговорът е верен.", "***")]
    public void Term_coverage_does_not_award_points_for_false_equivalents(string answer, string term)
    {
        var evaluation = new EvaluationCase("q1", "question", ["doc"], "reference", [term]);
        var result = new PipelineResult(
            "test",
            [new SearchHit("doc", "Doc", "1", "text", 0.9)],
            new GeneratedAnswer(answer, TokenUsage.Empty),
            TimeSpan.Zero,
            TimeSpan.Zero);

        var metrics = SearchMetrics.Calculate(evaluation, result, 5);

        Assert.Equal(0, metrics.RequiredTermCoverage, 6);
    }

    [Fact]
    public void Term_coverage_still_fails_on_genuinely_missing_fact()
    {
        var evaluation = new EvaluationCase("q1", "question", ["doc"], "reference", ["12%"]);
        var result = new PipelineResult(
            "test",
            [new SearchHit("doc", "Doc", "1", "text", 0.9)],
            new GeneratedAnswer("Крайната цена след отстъпката е 1600 лв.", TokenUsage.Empty),
            TimeSpan.Zero,
            TimeSpan.Zero);

        var metrics = SearchMetrics.Calculate(evaluation, result, 5);

        Assert.Equal(0, metrics.RequiredTermCoverage, 6);
    }

    [Fact]
    public void Calculate_returns_zeroes_for_failed_pipeline()
    {
        var evaluation = new EvaluationCase("q", "?", ["doc"], "answer", ["term"]);
        var result = PipelineResult.Failed("failed", new InvalidOperationException("boom"));

        var metrics = SearchMetrics.Calculate(evaluation, result, 3);

        Assert.False(metrics.HitAtK);
        Assert.Equal(0, metrics.MeanReciprocalRank);
        Assert.Equal(0, metrics.RequiredTermCoverage);
    }
}
