namespace SearchQuality.Domain;

public static class RelevanceThresholdFilter
{
    public static IReadOnlyList<SearchHit> Apply(
        IReadOnlyList<SearchHit> hits,
        double minimumScore,
        int maximumResults)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumScore, 0d);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumScore, 1d);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);

        return hits
            .Where(hit => hit.Relevance?.Score >= minimumScore)
            .Take(maximumResults)
            .ToArray();
    }
}
