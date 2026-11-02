using SearchQuality.Domain;

namespace SearchQuality.Application;

public static class SearchStrategyNames
{
    public const string FullText = "postgres-full-text";
    public const string PgVector = "postgres-pgvector";
    public const string Hybrid = "postgres-hybrid";
    public const string FileSearch = "openai-file-search";

    public static IReadOnlyList<string> All { get; } = [FullText, PgVector, Hybrid, FileSearch];
}

public sealed record InteractiveSearchResult(
    DateTimeOffset CreatedAt,
    string Question,
    int TopK,
    IReadOnlyList<PipelineResult> Results)
{
    public RelevanceEvaluationSummary? RelevanceEvaluation { get; init; }

    public double MinimumRelevanceScore { get; init; }
}

public sealed record RelevanceEvaluationSummary(
    string Model,
    TimeSpan Duration,
    TokenUsage Usage,
    string? Error);

public sealed record DiagnosticCheck(
    string Id,
    string Name,
    string State,
    string Summary,
    string? Detail,
    long DurationMilliseconds,
    IReadOnlyDictionary<string, string> Facts);

public sealed record LabDiagnostics(
    DateTimeOffset CheckedAt,
    string OverallState,
    IReadOnlyList<DiagnosticCheck> Checks);

public sealed record IndexOperationResult(
    string Operation,
    DateTimeOffset CompletedAt,
    string Message,
    int DocumentCount,
    int ChunkCount,
    string? VectorStoreId,
    IReadOnlyList<string> Warnings);
