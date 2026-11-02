using SearchQuality.Domain;
using SearchQuality.Reporting;

namespace SearchQuality.Tests.Reporting;

public sealed class BenchmarkReportWriterTests
{
    [Fact]
    public void CreateMarkdown_contains_strategy_metrics()
    {
        var report = new BenchmarkReport(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            5,
            [],
            [new StrategySummary("pgvector", 2, 0, 1, 0.2, 1, 0.75, 0.8, 123, 20, 5)
            {
                TotalEmbeddingTokens = 7
            }]);

        var markdown = BenchmarkReportWriter.CreateMarkdown(report);

        Assert.Contains("pgvector", markdown, StringComparison.Ordinal);
        Assert.Contains("100.0%", markdown, StringComparison.Ordinal);
        Assert.Contains("Precision@K", markdown, StringComparison.Ordinal);
        Assert.Contains("20.0%", markdown, StringComparison.Ordinal);
        Assert.Contains("Ключови факти", markdown, StringComparison.Ordinal);
        Assert.Contains("123", markdown, StringComparison.Ordinal);
        Assert.Contains("Embedding токени", markdown, StringComparison.Ordinal);
        Assert.Contains("32", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateMarkdown_explains_hybrid_source_ranks()
    {
        var hit = new SearchHit("doc", "Document", "chunk", "Text", 0.032522)
        {
            HybridRanks = new HybridRankDetails(1, 2, 60)
        };
        var evaluation = new EvaluationCase("q1", "Question", ["doc"], "Answer", []);
        var result = new PipelineResult(
            "postgres-hybrid",
            [hit],
            new GeneratedAnswer("Answer", TokenUsage.Empty),
            TimeSpan.Zero,
            TimeSpan.Zero);
        var report = new BenchmarkReport(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            5,
            [new BenchmarkCaseResult(evaluation, result, new CaseMetrics(true, 0.2, 1, 1, 1))],
            []);

        var markdown = BenchmarkReportWriter.CreateMarkdown(report);

        Assert.Contains("0.03252", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("99.2% съвпадение", markdown, StringComparison.Ordinal);
        Assert.Contains("raw RRF", markdown, StringComparison.Ordinal);
        Assert.Contains("Full-Text #1", markdown, StringComparison.Ordinal);
        Assert.Contains("pgvector #2", markdown, StringComparison.Ordinal);
    }
}
