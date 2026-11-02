using SearchQuality.Domain;

namespace SearchQuality.Tests.Domain;

public sealed class RelevanceThresholdFilterTests
{
    [Fact]
    public void Apply_accepts_exactly_forty_percent_and_rejects_lower_scores()
    {
        var hits = new[]
        {
            Hit("below", 0.399),
            Hit("boundary", 0.40),
            Hit("above", 0.85),
            new SearchHit("unrated", "Unrated", "unrated", "Text", 0.9)
        };

        var result = RelevanceThresholdFilter.Apply(hits, 0.40, maximumResults: 5);

        Assert.Equal(["boundary", "above"], result.Select(hit => hit.DocumentId));
    }

    [Fact]
    public void Apply_preserves_ranking_and_final_top_k_limit()
    {
        var hits = new[]
        {
            Hit("first", 0.60),
            Hit("second", 0.90),
            Hit("third", 1.00)
        };

        var result = RelevanceThresholdFilter.Apply(hits, 0.40, maximumResults: 2);

        Assert.Equal(["first", "second"], result.Select(hit => hit.DocumentId));
    }

    private static SearchHit Hit(string id, double relevance) =>
        new(id, id, id, $"Text for {id}", 0.5)
        {
            Relevance = new RelevanceAssessment(relevance, "Test")
        };
}
