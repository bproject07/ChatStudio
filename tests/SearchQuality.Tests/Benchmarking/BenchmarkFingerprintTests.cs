using SearchQuality.Benchmarking;
using SearchQuality.Domain;

namespace SearchQuality.Tests.Benchmarking;

public sealed class BenchmarkFingerprintTests
{
    [Fact]
    public void Document_fingerprint_is_order_independent_and_changes_with_content()
    {
        var first = new SourceDocument("a", "A", "a.md", "Alpha");
        var second = new SourceDocument("b", "B", "b.md", "Beta");

        var original = BenchmarkFingerprint.ForDocuments([first, second]);
        var reordered = BenchmarkFingerprint.ForDocuments([second, first]);
        var changed = BenchmarkFingerprint.ForDocuments([first with { Content = "Changed" }, second]);

        Assert.Equal(original, reordered);
        Assert.NotEqual(original, changed);
    }

    [Fact]
    public void Evaluation_fingerprint_changes_when_ground_truth_changes()
    {
        var evaluation = new EvaluationCase(
            "case",
            "Question",
            ["doc"],
            "Reference",
            ["term"]);

        var original = BenchmarkFingerprint.ForEvaluations([evaluation]);
        var changed = BenchmarkFingerprint.ForEvaluations([
            evaluation with { ExpectedDocumentIds = ["different"] }
        ]);

        Assert.NotEqual(original, changed);
    }
}
