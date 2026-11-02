using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SearchQuality.Configuration;
using SearchQuality.Domain;
using SearchQuality.Postgres;

namespace SearchQuality.Benchmarking;

public static class BenchmarkFingerprint
{
    /// <summary>
    /// Отпечатък на конфигурацията, от която зависят измерените стойности:
    /// модели, reasoning настройка, chunk размери, RRF константата и версията
    /// на експерименталния протокол (prompts, retrieval алгоритми — виж
    /// <see cref="BenchmarkProtocol"/>). Не включва тайни (API ключ, connection
    /// string) и не включва Top K, който е параметър на конкретното изпълнение
    /// и се пази в самия отчет.
    /// </summary>
    public static string ForConfiguration(LabSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var builder = new StringBuilder();
        Append(builder, "configuration-v1");
        Append(builder, BenchmarkProtocol.Version.ToString(CultureInfo.InvariantCulture));
        Append(builder, settings.OpenAI.BaseUrl);
        Append(builder, settings.OpenAI.GenerationBaseUrl ?? string.Empty);
        Append(builder, settings.OpenAI.GenerationUsesChatCompletions.ToString(CultureInfo.InvariantCulture));
        Append(builder, settings.OpenAI.GeneratorModel);
        Append(builder, settings.OpenAI.GeneratorReasoningEffort);
        Append(builder, settings.OpenAI.EmbeddingModel);
        Append(builder, settings.OpenAI.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture));
        Append(builder, settings.OpenAI.MaxOutputTokens.ToString(CultureInfo.InvariantCulture));
        Append(builder, settings.Benchmark.ChunkSizeWords.ToString(CultureInfo.InvariantCulture));
        Append(builder, settings.Benchmark.ChunkOverlapWords.ToString(CultureInfo.InvariantCulture));
        Append(builder, settings.OpenAI.FileSearchChunkSizeTokens.ToString(CultureInfo.InvariantCulture));
        Append(builder, settings.OpenAI.FileSearchChunkOverlapTokens.ToString(CultureInfo.InvariantCulture));
        Append(builder, ReciprocalRankFusion.DefaultRankConstant.ToString(CultureInfo.InvariantCulture));
        return Hash(builder);
    }

    public static string ForDocuments(IReadOnlyList<SourceDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var builder = new StringBuilder();
        Append(builder, "documents-v1");
        Append(builder, documents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var document in documents.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Append(builder, document.Id);
            Append(builder, document.Title);
            Append(builder, document.FileName);
            Append(builder, document.Content);
        }

        return Hash(builder);
    }

    public static string ForEvaluations(IReadOnlyList<EvaluationCase> evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        var builder = new StringBuilder();
        Append(builder, "evaluations-v1");
        Append(builder, evaluations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var evaluation in evaluations)
        {
            Append(builder, evaluation.Id);
            Append(builder, evaluation.Question);
            Append(
                builder,
                evaluation.ExpectedDocumentIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var documentId in evaluation.ExpectedDocumentIds)
            {
                Append(builder, documentId);
            }

            Append(builder, evaluation.ReferenceAnswer);
            Append(
                builder,
                evaluation.RequiredTerms.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var term in evaluation.RequiredTerms)
            {
                Append(builder, term);
            }
        }

        return Hash(builder);
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('|');

    private static string Hash(StringBuilder builder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
}
