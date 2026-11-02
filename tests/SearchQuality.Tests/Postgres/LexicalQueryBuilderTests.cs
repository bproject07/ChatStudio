using SearchQuality.Postgres;

namespace SearchQuality.Tests.Postgres;

public sealed class LexicalQueryBuilderTests
{
    [Fact]
    public void Build_removes_question_words_and_uses_prefix_or_search()
    {
        var query = LexicalQueryBuilder.Build("Колко дни платен годишен отпуск има служителят?");

        Assert.DoesNotContain("колко", query, StringComparison.Ordinal);
        Assert.DoesNotContain("има", query, StringComparison.Ordinal);
        Assert.Contains("дни:*", query, StringComparison.Ordinal);
        Assert.Contains(" | ", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_keeps_incident_codes_with_digits()
    {
        var query = LexicalQueryBuilder.Build("Какво правим при P1?");

        Assert.Contains("p1:*", query, StringComparison.Ordinal);
    }
}
