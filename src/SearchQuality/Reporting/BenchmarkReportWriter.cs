using System.Globalization;
using System.Text;
using System.Text.Json;
using SearchQuality.Application;
using SearchQuality.Domain;

namespace SearchQuality.Reporting;

public static class BenchmarkReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<IReadOnlyList<string>> WriteAsync(
        BenchmarkReport report,
        string directory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var timestamp = report.CreatedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(directory, $"benchmark-{timestamp}.json");
        var markdownPath = Path.Combine(directory, $"benchmark-{timestamp}.md");

        await using (var stream = File.Create(jsonPath))
        {
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken);
        }

        await File.WriteAllTextAsync(markdownPath, CreateMarkdown(report), cancellationToken);
        return [jsonPath, markdownPath];
    }

    public static string CreateMarkdown(BenchmarkReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Benchmark за качество на търсенето")
            .AppendLine()
            .Append("Създаден: ").AppendLine(report.CreatedAt.ToString("O", CultureInfo.InvariantCulture))
            .Append("Top K: ").AppendLine(report.TopK.ToString(CultureInfo.InvariantCulture))
            .Append("Ревизия на корпуса: ").AppendLine(ShortFingerprint(report.CorpusFingerprint))
            .Append("Ревизия на тестовете: ").AppendLine(ShortFingerprint(report.EvaluationFingerprint))
            .Append("Ревизия на конфигурацията: ").AppendLine(ShortFingerprint(report.ConfigurationFingerprint))
            .Append("Версия на метриките: ").AppendLine(report.MetricsVersion.ToString(CultureInfo.InvariantCulture))
            .AppendLine("Режим: суров benchmark Top K без интерактивния AI праг.")
            .AppendLine()
            .AppendLine("## Обобщение")
            .AppendLine()
            .AppendLine("| Метод | Успешни | Грешки | Hit@K | MRR | Recall@K | Doc Precision@K | Ключови факти | Retrieval ms | Средно време ms | GPT вход | GPT изход | GPT общо | Embedding токени | Общо токени |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var summary in report.Strategies)
        {
            builder.Append("| ").Append(Escape(summary.Strategy))
                .Append(" | ").Append(summary.SuccessfulCases)
                .Append(" | ").Append(summary.FailedCases)
                .Append(" | ").Append(Percent(summary.HitRateAtK))
                .Append(" | ").Append(summary.MeanReciprocalRank.ToString("0.000", CultureInfo.InvariantCulture))
                .Append(" | ").Append(Percent(summary.MeanRecallAtK))
                .Append(" | ").Append(Percent(summary.MeanPrecisionAtK))
                .Append(" | ").Append(Percent(summary.MeanRequiredTermCoverage))
                .Append(" | ").Append(RetrievalLatency(summary))
                .Append(" | ").Append(summary.MeanLatencyMilliseconds.ToString("0", CultureInfo.InvariantCulture))
                .Append(" | ").Append(summary.TotalInputTokens)
                .Append(" | ").Append(summary.TotalOutputTokens)
                .Append(" | ").Append(summary.TotalGptTokens)
                .Append(" | ").Append(summary.TotalEmbeddingTokens)
                .Append(" | ").Append(summary.TotalTokens)
                .AppendLine(" |");
        }

        builder.AppendLine().AppendLine("## Тестови случаи").AppendLine();
        foreach (var item in report.Cases)
        {
            builder.Append("### ").Append(item.Evaluation.Id).Append(" / ").AppendLine(item.Result.Strategy)
                .AppendLine()
                .Append("Въпрос: ").AppendLine(item.Evaluation.Question)
                .AppendLine();

            if (item.Result.Error is not null)
            {
                builder.Append("Грешка: ").AppendLine(item.Result.Error).AppendLine();
                continue;
            }

            builder.Append("Намерени източници: ")
                .AppendLine(string.Join(", ", item.Result.Hits.Select(hit => FormatHit(hit, item.Result.Strategy))))
                .Append("Hit@K: ").Append(item.Metrics.HitAtK)
                .Append("; MRR: ").Append(item.Metrics.MeanReciprocalRank.ToString("0.000", CultureInfo.InvariantCulture))
                .Append("; Recall@K: ").Append(Percent(item.Metrics.RecallAtK))
                .Append("; Doc Precision@K: ").Append(Percent(item.Metrics.PrecisionAtK))
                .Append("; ключови факти: ").AppendLine(Percent(item.Metrics.RequiredTermCoverage))
                .Append("Токени: GPT вход ").Append(item.Result.Answer.Usage.InputTokens)
                .Append("; GPT изход ").Append(item.Result.Answer.Usage.OutputTokens)
                .Append("; GPT общо ").Append(item.Result.Answer.Usage.TotalTokens)
                .Append("; Embedding ").Append(item.Result.EmbeddingUsage.TotalTokens)
                .Append("; Общо ").AppendLine(item.Result.TotalUsage.TotalTokens.ToString(CultureInfo.InvariantCulture))
                .AppendLine()
                .AppendLine(item.Result.Answer.Text)
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string Percent(double value) =>
        $"{(value * 100).ToString("0.0", CultureInfo.InvariantCulture)}%";

    private static string RetrievalLatency(StrategySummary summary) =>
        string.Equals(summary.Strategy, SearchStrategyNames.FileSearch, StringComparison.OrdinalIgnoreCase)
            ? "—"
            : summary.MeanRetrievalMilliseconds.ToString("0", CultureInfo.InvariantCulture);

    private static string FormatHit(SearchHit hit, string strategy)
    {
        var score = hit.Score.ToString(
            string.Equals(strategy, SearchStrategyNames.Hybrid, StringComparison.OrdinalIgnoreCase)
                ? "0.00000"
                : "0.000",
            CultureInfo.InvariantCulture);
        if (hit.HybridRanks is null)
        {
            return $"{hit.DocumentId} ({TechnicalScoreName(strategy)} {score})";
        }

        var fullTextRank = hit.HybridRanks.FullTextRank?.ToString(CultureInfo.InvariantCulture) ?? "—";
        var vectorRank = hit.HybridRanks.VectorRank?.ToString(CultureInfo.InvariantCulture) ?? "—";
        return $"{hit.DocumentId} (raw RRF {score}; Full-Text #{fullTextRank}; pgvector #{vectorRank})";
    }

    private static string TechnicalScoreName(string strategy) => strategy switch
    {
        SearchStrategyNames.FullText => "ts_rank_cd",
        SearchStrategyNames.PgVector => "cosine",
        SearchStrategyNames.FileSearch => "File Search raw",
        _ => "raw"
    };

    private static string ShortFingerprint(string? fingerprint) =>
        string.IsNullOrWhiteSpace(fingerprint)
            ? "стар формат — липсва"
            : fingerprint[..Math.Min(12, fingerprint.Length)];

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
