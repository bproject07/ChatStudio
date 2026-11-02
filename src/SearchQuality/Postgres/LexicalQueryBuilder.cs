using System.Text.RegularExpressions;

namespace SearchQuality.Postgres;

public static partial class LexicalQueryBuilder
{
    private static readonly HashSet<string> BulgarianStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ако", "без", "беше", "биха", "в", "вече", "ви", "вие", "всички", "го", "да", "до",
        "е", "за", "и", "или", "има", "как", "каква", "какви", "какво", "какъв", "кога", "кое",
        "кои", "кой", "колко", "която", "което", "които", "ли", "ми", "мога", "може", "му", "на",
        "не", "ни", "но", "още", "от", "по", "при", "са", "се", "си", "сме", "съм", "със", "тази",
        "тези", "това", "този", "трябва", "я"
    };

    public static string Build(string naturalLanguageQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(naturalLanguageQuery);

        var terms = WordRegex()
            .Matches(naturalLanguageQuery.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(term => term.Length >= 3 || term.Any(char.IsDigit))
            .Where(term => !BulgarianStopWords.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(term => $"{term}:*")
            .ToArray();

        return string.Join(" | ", terms);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
