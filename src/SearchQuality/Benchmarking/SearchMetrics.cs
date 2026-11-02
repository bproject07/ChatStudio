using System.Text;
using System.Text.RegularExpressions;
using SearchQuality.Domain;

namespace SearchQuality.Benchmarking;

public static class SearchMetrics
{
    /// <summary>
    /// Версия на дефинициите на метриките, записвана във всеки отчет.
    /// Версия 1: Precision@K = правилни chunk-ове / фиксирано K (не се записваше).
    /// Версия 2: Doc Precision@K = различни очаквани / различни върнати документа.
    /// Версия 3: покритието на контролни фрази нормализира отговора и фразата
    /// преди сравнението — маха markdown emphasis, свива празните знаци и
    /// изравнява „дена“→„дни“ и числителните думи до цифри.
    /// Отчет с различна версия не е директно сравним с текущите резултати.
    /// </summary>
    public const int CurrentMetricsVersion = 3;

    private static readonly Regex MultipleSpaces = new(" {2,}", RegexOptions.Compiled);

    // Изравнява най-честите преразкази, които не променят факта: GPT често
    // изписва „три“ като „3“ или „45 дни“ като „45 дена“. Словоредът и
    // синонимите нарочно не се нормализират — там е нужен човешки преглед.
    private static readonly (Regex Pattern, string Replacement)[] TermEquivalents =
    [
        (new Regex(@"\bдена\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "дни"),
        (new Regex(@"\bедин\b|\bедна\b|\bедно\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "1"),
        (new Regex(@"\bдве\b|\bдва\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "2"),
        (new Regex(@"\bтри\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "3"),
        (new Regex(@"\bчетири\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "4"),
        (new Regex(@"\bпет\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "5"),
        (new Regex(@"\bшест\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "6"),
        (new Regex(@"\bседем\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "7"),
        (new Regex(@"\bосем\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "8"),
        (new Regex(@"\bдевет\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "9"),
        (new Regex(@"\bдесет\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "10")
    ];

    public static CaseMetrics Calculate(EvaluationCase evaluation, PipelineResult result, int topK)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);

        if (result.Error is not null)
        {
            return new CaseMetrics(false, 0, 0, 0, 0);
        }

        var expected = evaluation.ExpectedDocumentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hits = result.Hits.Take(topK).ToArray();
        var relevantHitCount = hits.Count(hit => expected.Contains(hit.DocumentId));
        // Precision и Recall се смятат на документно ниво, за да не могат
        // няколко припокриващи се chunk-а от един документ да надуват оценката
        // и за да са сравними стратегии с различно chunk-ване.
        var distinctRetrievedDocuments = hits
            .Select(hit => hit.DocumentId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var distinctRelevantDocuments = hits
            .Where(hit => expected.Contains(hit.DocumentId))
            .Select(hit => hit.DocumentId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var firstRelevantIndex = Array.FindIndex(hits, hit => expected.Contains(hit.DocumentId));
        var reciprocalRank = firstRelevantIndex < 0 ? 0 : 1d / (firstRelevantIndex + 1);
        // Отговорът и фразата се нормализират еднакво: иначе „до **8 часа**“
        // (markdown) или „3 касови места“ (цифра вместо дума) се броят за
        // липсващи, въпреки че фактът е дословно наличен.
        var normalizedAnswer = NormalizeForTermMatch(result.Answer.Text);
        var requiredTermCoverage = evaluation.RequiredTerms.Count == 0
            ? 1
            : evaluation.RequiredTerms.Count(term =>
              {
                  // Фраза, останала празна след нормализацията (напр. само "***"),
                  // не носи точка: Contains("") винаги връща true.
                  var normalizedTerm = NormalizeForTermMatch(term).Trim();
                  return normalizedTerm.Length > 0
                      && normalizedAnswer.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase);
              })
              / (double)evaluation.RequiredTerms.Count;

        return new CaseMetrics(
            relevantHitCount > 0,
            distinctRetrievedDocuments == 0
                ? 0
                : distinctRelevantDocuments / (double)distinctRetrievedDocuments,
            expected.Count == 0 ? 1 : distinctRelevantDocuments / (double)expected.Count,
            reciprocalRank,
            requiredTermCoverage);
    }

    private static string NormalizeForTermMatch(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '*' or '_' or '`')
            {
                continue;
            }

            builder.Append(char.IsWhiteSpace(character) ? ' ' : character);
        }

        var normalized = MultipleSpaces.Replace(builder.ToString(), " ");
        foreach (var (pattern, replacement) in TermEquivalents)
        {
            normalized = pattern.Replace(normalized, replacement);
        }

        return normalized;
    }
}
