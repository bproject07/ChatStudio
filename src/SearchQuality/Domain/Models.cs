namespace SearchQuality.Domain;

public sealed record SourceDocument(
    string Id,
    string Title,
    string FileName,
    string Content);

public sealed record DocumentChunk(
    string Id,
    string DocumentId,
    string DocumentTitle,
    int Ordinal,
    string Text);

public sealed record SearchHit(
    string DocumentId,
    string DocumentTitle,
    string ChunkId,
    string Text,
    double Score)
{
    public HybridRankDetails? HybridRanks { get; init; }

    public RelevanceAssessment? Relevance { get; init; }
}

public sealed record RelevanceAssessment(
    double Score,
    string Reason);

public sealed record RelevanceJudgment(
    int CandidateId,
    double Score,
    string Reason);

public sealed record RelevanceJudgeResult(
    IReadOnlyList<RelevanceJudgment> Judgments,
    TokenUsage Usage);

public sealed record HybridRankDetails(
    int? FullTextRank,
    int? VectorRank,
    int RankConstant);

public sealed record EmbeddingResult(
    IReadOnlyList<float[]> Vectors,
    TokenUsage Usage);

public sealed record RetrievalResult(
    IReadOnlyList<SearchHit> Hits,
    TokenUsage EmbeddingUsage);

public sealed record EvaluationCase(
    string Id,
    string Question,
    IReadOnlyList<string> ExpectedDocumentIds,
    string ReferenceAnswer,
    IReadOnlyList<string> RequiredTerms);

public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens)
{
    public static TokenUsage Empty { get; } = new(0, 0, 0);

    public static TokenUsage Combine(TokenUsage first, TokenUsage second) =>
        new(
            first.InputTokens + second.InputTokens,
            first.OutputTokens + second.OutputTokens,
            first.TotalTokens + second.TotalTokens);
}

public sealed record GeneratedAnswer(
    string Text,
    TokenUsage Usage,
    int? ContextInputTokens = null);

public sealed record PipelineResult(
    string Strategy,
    IReadOnlyList<SearchHit> Hits,
    GeneratedAnswer Answer,
    TimeSpan RetrievalDuration,
    TimeSpan GenerationDuration,
    string? Error = null)
{
    public TokenUsage EmbeddingUsage { get; init; } = TokenUsage.Empty;

    public int RejectedHitCount { get; init; }

    public TokenUsage TotalUsage => TokenUsage.Combine(Answer.Usage, EmbeddingUsage);

    public TimeSpan TotalDuration => RetrievalDuration + GenerationDuration;

    public static PipelineResult Failed(string strategy, Exception exception) =>
        new(
            strategy,
            [],
            new GeneratedAnswer(string.Empty, TokenUsage.Empty),
            TimeSpan.Zero,
            TimeSpan.Zero,
            exception.Message);
}

public sealed record CaseMetrics(
    bool HitAtK,
    double PrecisionAtK,
    double RecallAtK,
    double MeanReciprocalRank,
    double RequiredTermCoverage);

public sealed record BenchmarkCaseResult(
    EvaluationCase Evaluation,
    PipelineResult Result,
    CaseMetrics Metrics);

public sealed record StrategySummary(
    string Strategy,
    int SuccessfulCases,
    int FailedCases,
    double HitRateAtK,
    double MeanPrecisionAtK,
    double MeanRecallAtK,
    double MeanReciprocalRank,
    double MeanRequiredTermCoverage,
    double MeanLatencyMilliseconds,
    int TotalInputTokens,
    int TotalOutputTokens)
{
    /// <summary>
    /// Средно retrieval-only време за успешните случаи. За OpenAI File Search
    /// стойността е 0, защото управляваният поток не отделя retrieval времето.
    /// </summary>
    public double MeanRetrievalMilliseconds { get; init; }

    public int TotalEmbeddingTokens { get; init; }

    public int TotalGptTokens => TotalInputTokens + TotalOutputTokens;

    public int TotalTokens => TotalGptTokens + TotalEmbeddingTokens;
}

public sealed record BenchmarkReport(
    DateTimeOffset CreatedAt,
    int TopK,
    IReadOnlyList<BenchmarkCaseResult> Cases,
    IReadOnlyList<StrategySummary> Strategies)
{
    public string? CorpusFingerprint { get; init; }

    public string? EvaluationFingerprint { get; init; }

    /// <summary>
    /// Отпечатък на конфигурацията (модели, reasoning, chunk размери, RRF
    /// константа), с която е изпълнен отчетът. „Актуален“ изисква съвпадение —
    /// два отчета с еднакъв корпус, но различни модели, не са сравними.
    /// </summary>
    public string? ConfigurationFingerprint { get; init; }

    /// <summary>
    /// Версията на метриките, с които е сметнат отчетът. Отчети отпреди
    /// въвеждането на полето се десериализират като 0 и се третират като
    /// исторически, защото формулите им не са сравними с текущите.
    /// </summary>
    public int MetricsVersion { get; init; }
}
