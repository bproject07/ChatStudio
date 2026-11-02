using System.Diagnostics;
using SearchQuality.Abstractions;
using SearchQuality.Benchmarking;
using SearchQuality.Configuration;
using SearchQuality.Corpus;
using SearchQuality.Domain;
using SearchQuality.OpenAI;
using SearchQuality.Postgres;

namespace SearchQuality.Application;

public sealed class SearchQualityLabService
{
    private const double MinimumInteractiveRelevanceScore = 0.40d;
    private const string NoRelevantSourcesMessage =
        "Няма намерени източници с поне 40% AI релевантност. GPT отговор не е генериран.";
    private readonly LabSettings _settings;
    private readonly OpenAiApiClient _openAiClient;
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);

    public SearchQualityLabService(LabSettings settings, OpenAiApiClient openAiClient)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(openAiClient);
        _settings = settings;
        _openAiClient = openAiClient;
    }

    public LabSettings Settings => _settings;

    public Task<IReadOnlyList<SourceDocument>> GetDocumentsAsync(
        CancellationToken cancellationToken,
        bool allowEmpty = false) =>
        CorpusLoader.LoadDocumentsAsync(_settings.DocumentsDirectory, cancellationToken, allowEmpty);

    public Task<IReadOnlyList<EvaluationCase>> GetEvaluationsAsync(CancellationToken cancellationToken) =>
        CorpusLoader.LoadEvaluationCasesAsync(_settings.EvaluationsPath, cancellationToken);

    public async Task<InteractiveSearchResult> SearchAsync(
        string question,
        int topK,
        IReadOnlyList<string>? strategies,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(topK, 20);

        var normalizedQuestion = question.Trim();
        var selected = NormalizeStrategies(strategies);
        var pipelines = await CreatePipelinesAsync(selected, cancellationToken);
        var stagedPipelines = pipelines.OfType<RetrievalAugmentedPipeline>().ToArray();
        var directPipelines = pipelines.Where(pipeline => pipeline is not RetrievalAugmentedPipeline).ToArray();
        var stagedTasks = stagedPipelines.Select(pipeline => RetrieveSafelyAsync(
            pipeline,
            normalizedQuestion,
            topK,
            cancellationToken));
        var directTasks = directPipelines.Select(pipeline => ExecuteSafelyAsync(
            pipeline,
            normalizedQuestion,
            topK,
            cancellationToken));
        var stagedRetrievals = await Task.WhenAll(stagedTasks);
        var directResults = await Task.WhenAll(directTasks);
        RelevanceEvaluationSummary? relevanceEvaluation = null;
        var uniqueCandidates = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        foreach (var hit in stagedRetrievals
                     .Where(item => item.Context is not null)
                     .SelectMany(item => item.Context!.Retrieval.Hits)
                     .Concat(directResults
                         .Where(result => result.Error is null)
                         .SelectMany(result => result.Hits)))
        {
            uniqueCandidates.TryAdd(RelevanceCandidateKey(hit), hit);
        }

        Dictionary<string, RelevanceAssessment>? assessments = null;
        if (uniqueCandidates.Count > 0)
        {
            var candidates = uniqueCandidates.Values.ToArray();
            var timer = Stopwatch.StartNew();
            try
            {
                var judgment = await _openAiClient.JudgeRelevanceAsync(
                    normalizedQuestion,
                    candidates,
                    cancellationToken);
                timer.Stop();
                var judgments = judgment.Judgments.ToDictionary(item => item.CandidateId);
                assessments = candidates
                    .Select((hit, index) => new
                    {
                        Key = RelevanceCandidateKey(hit),
                        Judgment = judgments[index + 1]
                    })
                    .ToDictionary(
                        item => item.Key,
                        item => new RelevanceAssessment(
                            item.Judgment.Score,
                            item.Judgment.Reason),
                        StringComparer.Ordinal);
                relevanceEvaluation = new RelevanceEvaluationSummary(
                    _settings.OpenAI.JudgeModel,
                    timer.Elapsed,
                    judgment.Usage,
                    null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                timer.Stop();
                relevanceEvaluation = new RelevanceEvaluationSummary(
                    _settings.OpenAI.JudgeModel,
                    timer.Elapsed,
                    TokenUsage.Empty,
                    exception.Message);
            }
        }

        var stagedCompletionTasks = stagedRetrievals.Select(item => CompleteStagedSearchAsync(
            item,
            normalizedQuestion,
            topK,
            assessments,
            relevanceEvaluation?.Error,
            cancellationToken));
        var stagedResults = await Task.WhenAll(stagedCompletionTasks);
        var filteredDirectResults = directResults.Select(result => FilterDirectResult(
            result,
            topK,
            assessments,
            relevanceEvaluation?.Error)).ToArray();
        var resultsByStrategy = stagedResults
            .Concat(filteredDirectResults)
            .ToDictionary(result => result.Strategy, StringComparer.OrdinalIgnoreCase);
        var results = selected.Select(strategy => resultsByStrategy[strategy]).ToArray();

        return new InteractiveSearchResult(
            DateTimeOffset.UtcNow,
            normalizedQuestion,
            topK,
            results)
        {
            RelevanceEvaluation = relevanceEvaluation,
            MinimumRelevanceScore = MinimumInteractiveRelevanceScore
        };
    }

    public async Task<BenchmarkReport> RunBenchmarkAsync(
        int? caseLimit,
        int topK,
        IReadOnlyList<string>? strategies,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(topK, 20);
        if (caseLimit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(caseLimit), "Case limit must be positive.");
        }

        var documents = await GetDocumentsAsync(cancellationToken, allowEmpty: true);
        var evaluations = await GetEvaluationsAsync(cancellationToken);
        if (caseLimit is not null)
        {
            evaluations = evaluations.Take(caseLimit.Value).ToArray();
        }

        var selected = NormalizeStrategies(strategies);
        var pipelines = await CreatePipelinesAsync(selected, cancellationToken);
        var report = await new BenchmarkRunner(pipelines, topK)
            .RunAsync(evaluations, progress, cancellationToken);
        return report with
        {
            CorpusFingerprint = BenchmarkFingerprint.ForDocuments(documents),
            EvaluationFingerprint = BenchmarkFingerprint.ForEvaluations(evaluations),
            ConfigurationFingerprint = BenchmarkFingerprint.ForConfiguration(_settings)
        };
    }

    public async Task<LabDiagnostics> DiagnoseAsync(CancellationToken cancellationToken)
    {
        var corpusTask = DiagnoseCorpusAsync(cancellationToken);
        var openAiTask = DiagnoseOpenAiAsync(cancellationToken);
        var postgresTask = DiagnosePostgresAsync(cancellationToken);
        var fileSearchTask = DiagnoseFileSearchAsync(cancellationToken);
        var checks = await Task.WhenAll(corpusTask, openAiTask, postgresTask, fileSearchTask);
        var overall = checks.Any(check => check.State == "error")
            ? "error"
            : checks.Any(check => check.State == "warning")
                ? "warning"
                : "healthy";

        return new LabDiagnostics(DateTimeOffset.UtcNow, overall, checks);
    }

    public async Task<IndexOperationResult> IndexPostgresAsync(
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            var documents = await GetDocumentsAsync(cancellationToken, allowEmpty: true);
            var hashes = ComputeHashes(documents, CorpusSync.PostgresSignature(_settings));
            var chunks = ChunkDocuments(documents);
            var embeddings = await EmbedChunksAsync(chunks, progress, cancellationToken);

            var store = new PostgresSearchStore(_settings.Postgres);
            progress?.Invoke("Подготовка на PostgreSQL схемата...");
            await store.InitializeAsync(_settings.OpenAI.EmbeddingDimensions, cancellationToken);
            await store.ReplaceCorpusAsync(documents, hashes, chunks, embeddings, cancellationToken);

            return new IndexOperationResult(
                "index-postgres",
                DateTimeOffset.UtcNow,
                "PostgreSQL Full-Text и pgvector индексите са обновени.",
                documents.Count,
                chunks.Count,
                null,
                []);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    public async Task<IndexOperationResult> SyncPostgresAsync(
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            var documents = await GetDocumentsAsync(cancellationToken, allowEmpty: true);
            var signature = CorpusSync.PostgresSignature(_settings);
            var store = new PostgresSearchStore(_settings.Postgres);
            progress?.Invoke("Подготовка на PostgreSQL схемата...");
            await store.InitializeAsync(_settings.OpenAI.EmbeddingDimensions, cancellationToken);
            var diff = CorpusSync.Compute(
                documents,
                await store.GetDocumentHashesAsync(cancellationToken),
                signature);
            if (diff.IsEmpty)
            {
                return new IndexOperationResult(
                    "sync-postgres",
                    DateTimeOffset.UtcNow,
                    $"PostgreSQL индексът е актуален: {diff.UnchangedCount} документа без промяна.",
                    documents.Count,
                    0,
                    null,
                    []);
            }

            var toIndex = diff.ToIndex;
            var hashes = ComputeHashes(toIndex, signature);
            var chunks = ChunkDocuments(toIndex);
            var embeddings = await EmbedChunksAsync(chunks, progress, cancellationToken);
            await store.SyncCorpusAsync(
                toIndex,
                hashes,
                chunks,
                embeddings,
                diff.RemovedIds,
                cancellationToken);

            return new IndexOperationResult(
                "sync-postgres",
                DateTimeOffset.UtcNow,
                $"PostgreSQL индексът е синхронизиран: {diff.Describe()}.",
                documents.Count,
                chunks.Count,
                null,
                []);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    public async Task<IndexOperationResult> RebuildFileSearchAsync(
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            return await RebuildFileSearchCoreAsync("rebuild-file-search", progress, cancellationToken);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    public async Task<IndexOperationResult> SyncFileSearchAsync(
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            var state = await FileSearchStateStore.LoadAsync(_settings.StatePath, cancellationToken);
            if (state?.Files is null || !await VectorStoreIsUsableAsync(state, cancellationToken))
            {
                // Няма индекс, той е от преди картата документ→файл, или Store-ът
                // е изтекъл/изтрит: разликата не може да се приложи надеждно,
                // затова еднократно се минава през пълно преизграждане.
                progress?.Invoke("Няма годен индекс за синхронизиране — пълно изграждане...");
                return await RebuildFileSearchCoreAsync("sync-file-search", progress, cancellationToken);
            }

            var documents = await GetDocumentsAsync(cancellationToken, allowEmpty: true);
            if (documents.Count == 0)
            {
                throw new InvalidOperationException(
                    "Няма локални документи за OpenAI File Search. " +
                    "Използвайте „Изтрий File Search“, за да премахнете стария индекс.");
            }

            var signature = CorpusSync.FileSearchSignature(_settings);
            var indexedHashes = state.Files.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ContentHash,
                StringComparer.Ordinal);
            var diff = CorpusSync.Compute(documents, indexedHashes, signature);

            // Качените файлове изтичат 7 дни след създаването си (safety net
            // срещу осиротели ресурси), затова near-изтичащите се качват наново
            // дори без промяна по съдържанието.
            var expiring = documents
                .Where(document => !diff.ToIndex.Any(item => item.Id == document.Id)
                    && state.Files.TryGetValue(document.Id, out var entry)
                    && entry.UploadedAt < DateTimeOffset.UtcNow.AddDays(-6))
                .ToArray();
            var toIndex = diff.ToIndex.Concat(expiring).ToArray();
            if (toIndex.Length == 0 && diff.RemovedIds.Count == 0)
            {
                return new IndexOperationResult(
                    "sync-file-search",
                    DateTimeOffset.UtcNow,
                    $"OpenAI File Search индексът е актуален: {diff.UnchangedCount} документа без промяна.",
                    documents.Count,
                    0,
                    state.VectorStoreId,
                    []);
            }

            var newState = await _openAiClient.SyncFileSearchIndexAsync(
                state,
                toIndex,
                ComputeHashes(toIndex, signature),
                diff.RemovedIds,
                progress,
                cancellationToken);
            await FileSearchStateStore.SaveAsync(_settings.StatePath, newState, cancellationToken);

            var refreshedNote = expiring.Length > 0 ? $", опреснени преди изтичане {expiring.Length}" : string.Empty;
            return new IndexOperationResult(
                "sync-file-search",
                DateTimeOffset.UtcNow,
                $"OpenAI File Search индексът е синхронизиран: {diff.Describe()}{refreshedNote}.",
                documents.Count,
                0,
                newState.VectorStoreId,
                []);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    private async Task<bool> VectorStoreIsUsableAsync(
        FileSearchIndexState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await _openAiClient.GetVectorStoreStatusAsync(
                state.VectorStoreId,
                cancellationToken);
            return string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.Status, "in_progress", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<IndexOperationResult> RebuildFileSearchCoreAsync(
        string operation,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var documents = await GetDocumentsAsync(cancellationToken, allowEmpty: true);
        if (documents.Count == 0)
        {
            throw new InvalidOperationException(
                "Няма локални документи за OpenAI File Search. " +
                "Използвайте „Изтрий File Search“, за да премахнете стария индекс.");
        }
        var previousState = await FileSearchStateStore.LoadAsync(_settings.StatePath, cancellationToken);
        progress?.Invoke("Създаване на нов OpenAI Vector Store...");
        var newState = await _openAiClient.CreateFileSearchIndexAsync(
            $"search-quality-lab-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}",
            documents,
            ComputeHashes(documents, CorpusSync.FileSearchSignature(_settings)),
            progress,
            cancellationToken);
        await FileSearchStateStore.SaveAsync(_settings.StatePath, newState, cancellationToken);

        var warnings = new List<string>();
        if (previousState is not null
            && !string.Equals(
                previousState.VectorStoreId,
                newState.VectorStoreId,
                StringComparison.Ordinal))
        {
            try
            {
                progress?.Invoke("Премахване на предишния лабораторен индекс...");
                await _openAiClient.DeleteFileSearchIndexAsync(
                    previousState,
                    progress,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"Новият индекс е активен, но старият не беше изтрит: {exception.Message}");
            }
        }

        return new IndexOperationResult(
            operation,
            DateTimeOffset.UtcNow,
            "OpenAI File Search индексът е създаден и активиран.",
            documents.Count,
            0,
            newState.VectorStoreId,
            warnings);
    }

    private IReadOnlyList<DocumentChunk> ChunkDocuments(IReadOnlyList<SourceDocument> documents)
    {
        var chunker = new ApproximateTokenChunker(
            _settings.Benchmark.ChunkSizeWords,
            _settings.Benchmark.ChunkOverlapWords);
        return documents.SelectMany(chunker.Chunk).ToArray();
    }

    private async Task<IReadOnlyList<float[]>> EmbedChunksAsync(
        IReadOnlyList<DocumentChunk> chunks,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        var embeddings = new List<float[]>(chunks.Count);
        const int batchSize = 64;
        for (var offset = 0; offset < chunks.Count; offset += batchSize)
        {
            var batch = chunks
                .Skip(offset)
                .Take(batchSize)
                .Select(chunk => $"{chunk.DocumentTitle}\n{chunk.Text}")
                .ToArray();
            var embedding = await _openAiClient.CreateEmbeddingsAsync(batch, cancellationToken);
            embeddings.AddRange(embedding.Vectors);
            progress?.Invoke($"Векторизирани части: {embeddings.Count}/{chunks.Count}");
        }

        return embeddings;
    }

    private static IReadOnlyDictionary<string, string> ComputeHashes(
        IReadOnlyList<SourceDocument> documents,
        string signature) =>
        documents.ToDictionary(
            document => document.Id,
            document => CorpusSync.ComputeDocumentHash(document, signature),
            StringComparer.Ordinal);

    public async Task<IndexOperationResult> CleanupFileSearchAsync(
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        await _maintenanceLock.WaitAsync(cancellationToken);
        try
        {
            var state = await FileSearchStateStore.LoadAsync(_settings.StatePath, cancellationToken);
            if (state is null)
            {
                return new IndexOperationResult(
                    "cleanup-file-search",
                    DateTimeOffset.UtcNow,
                    "Няма записан лабораторен OpenAI индекс за изтриване.",
                    0,
                    0,
                    null,
                    []);
            }

            await _openAiClient.DeleteFileSearchIndexAsync(state, progress, cancellationToken);
            File.Delete(_settings.StatePath);
            return new IndexOperationResult(
                "cleanup-file-search",
                DateTimeOffset.UtcNow,
                "OpenAI Vector Store и лабораторните файлове са изтрити.",
                0,
                0,
                state.VectorStoreId,
                []);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    private async Task<IReadOnlyList<ISearchPipeline>> CreatePipelinesAsync(
        IReadOnlyList<string> selected,
        CancellationToken cancellationToken)
    {
        PostgresSearchStore? store = null;
        if (selected.Any(name => name is
                SearchStrategyNames.FullText or
                SearchStrategyNames.PgVector or
                SearchStrategyNames.Hybrid))
        {
            store = new PostgresSearchStore(_settings.Postgres);
        }

        var state = selected.Contains(SearchStrategyNames.FileSearch, StringComparer.OrdinalIgnoreCase)
            ? await FileSearchStateStore.LoadAsync(_settings.StatePath, cancellationToken)
            : null;
        var pipelines = new List<ISearchPipeline>(selected.Count);

        foreach (var name in selected)
        {
            pipelines.Add(name switch
            {
                SearchStrategyNames.FullText => new RetrievalAugmentedPipeline(
                    new PostgresFullTextRetriever(store!),
                    _openAiClient),
                SearchStrategyNames.PgVector => new RetrievalAugmentedPipeline(
                    new PostgresVectorRetriever(store!, _openAiClient),
                    _openAiClient),
                SearchStrategyNames.Hybrid => new RetrievalAugmentedPipeline(
                    new PostgresHybridRetriever(store!, _openAiClient),
                    _openAiClient),
                SearchStrategyNames.FileSearch when state is not null => new OpenAiFileSearchPipeline(
                    _openAiClient,
                    state.VectorStoreId),
                SearchStrategyNames.FileSearch => new UnavailablePipeline(
                    SearchStrategyNames.FileSearch,
                    "OpenAI File Search индексът липсва. Създайте го от секцията Данни."),
                _ => throw new UnreachableException()
            });
        }

        return pipelines;
    }

    private static IReadOnlyList<string> NormalizeStrategies(IReadOnlyList<string>? strategies)
    {
        if (strategies is null || strategies.Count == 0)
        {
            return SearchStrategyNames.All;
        }

        // Имената се приемат без значение на регистъра, но по-нататък dispatch-ът
        // работи с каноничните идентификатори, затова се нормализират тук.
        var selected = new List<string>();
        foreach (var name in strategies.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var canonical = SearchStrategyNames.All.FirstOrDefault(known =>
                    string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown search strategy: {name}", nameof(strategies));
            if (!selected.Contains(canonical, StringComparer.Ordinal))
            {
                selected.Add(canonical);
            }
        }

        return selected;
    }

    private static async Task<PipelineResult> ExecuteSafelyAsync(
        ISearchPipeline pipeline,
        string question,
        int topK,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pipeline.ExecuteAsync(question, topK, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PipelineResult.Failed(pipeline.Name, exception);
        }
    }

    private static async Task<StagedRetrieval> RetrieveSafelyAsync(
        RetrievalAugmentedPipeline pipeline,
        string question,
        int topK,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = await pipeline.RetrieveAsync(question, topK, cancellationToken);
            return new StagedRetrieval(pipeline, context, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new StagedRetrieval(pipeline, null, exception);
        }
    }

    private static async Task<PipelineResult> CompleteStagedSearchAsync(
        StagedRetrieval staged,
        string question,
        int topK,
        IReadOnlyDictionary<string, RelevanceAssessment>? assessments,
        string? relevanceError,
        CancellationToken cancellationToken)
    {
        if (staged.Error is not null)
        {
            return PipelineResult.Failed(staged.Pipeline.Name, staged.Error);
        }

        var context = staged.Context!;
        if (relevanceError is not null && context.Retrieval.Hits.Count > 0)
        {
            return FailedAfterRetrieval(
                staged.Pipeline.Name,
                context,
                $"AI оценката за релевантност не успя; GPT не получи неоценени източници. {relevanceError}");
        }

        var evaluatedHits = ApplyRelevanceAssessments(context.Retrieval.Hits, assessments);
        var acceptedHits = RelevanceThresholdFilter.Apply(
            evaluatedHits,
            MinimumInteractiveRelevanceScore,
            topK);
        var rejectedCount = evaluatedHits.Count - acceptedHits.Count;

        if (acceptedHits.Count == 0)
        {
            return new PipelineResult(
                staged.Pipeline.Name,
                [],
                new GeneratedAnswer(NoRelevantSourcesMessage, TokenUsage.Empty),
                context.Duration,
                TimeSpan.Zero)
            {
                EmbeddingUsage = context.Retrieval.EmbeddingUsage,
                RejectedHitCount = rejectedCount
            };
        }

        try
        {
            var result = await staged.Pipeline.GenerateAsync(
                question,
                context,
                acceptedHits,
                cancellationToken);
            return result with { RejectedHitCount = rejectedCount };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PipelineResult.Failed(staged.Pipeline.Name, exception) with
            {
                RejectedHitCount = rejectedCount
            };
        }
    }

    private static PipelineResult FilterDirectResult(
        PipelineResult result,
        int topK,
        IReadOnlyDictionary<string, RelevanceAssessment>? assessments,
        string? relevanceError)
    {
        if (result.Error is not null || result.Hits.Count == 0)
        {
            return result;
        }

        if (relevanceError is not null)
        {
            // Токените остават отчетени, но неоцененият отговор не се показва,
            // за да не цитира източници, които интерфейсът вече не визуализира.
            return result with
            {
                Hits = [],
                Answer = result.Answer with { Text = string.Empty },
                Error = $"AI оценката за релевантност не успя. {relevanceError}"
            };
        }

        var evaluatedHits = ApplyRelevanceAssessments(result.Hits, assessments);
        var acceptedHits = RelevanceThresholdFilter.Apply(
            evaluatedHits,
            MinimumInteractiveRelevanceScore,
            topK);
        var rejectedCount = evaluatedHits.Count - acceptedHits.Count;
        return result with
        {
            Hits = acceptedHits,
            Answer = acceptedHits.Count == 0
                ? result.Answer with { Text = NoRelevantSourcesMessage }
                : result.Answer,
            RejectedHitCount = rejectedCount
        };
    }

    private static IReadOnlyList<SearchHit> ApplyRelevanceAssessments(
        IReadOnlyList<SearchHit> hits,
        IReadOnlyDictionary<string, RelevanceAssessment>? assessments) =>
        hits.Select(hit => assessments is not null
                           && assessments.TryGetValue(RelevanceCandidateKey(hit), out var assessment)
            ? hit with { Relevance = assessment }
            : hit).ToArray();

    private static PipelineResult FailedAfterRetrieval(
        string strategy,
        RetrievedContext context,
        string error) =>
        new(
            strategy,
            [],
            new GeneratedAnswer(string.Empty, TokenUsage.Empty),
            context.Duration,
            TimeSpan.Zero,
            error)
        {
            EmbeddingUsage = context.Retrieval.EmbeddingUsage
        };

    private static string RelevanceCandidateKey(SearchHit hit) =>
        string.Concat(hit.DocumentId, "\u001f", hit.ChunkId, "\u001f", hit.Text);

    private async Task<DiagnosticCheck> DiagnoseCorpusAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var documents = await GetDocumentsAsync(cancellationToken, allowEmpty: true);
            var evaluations = await GetEvaluationsAsync(cancellationToken);
            timer.Stop();
            var hasDocuments = documents.Count > 0;
            return new DiagnosticCheck(
                "corpus",
                "Тестови данни",
                hasDocuments ? "healthy" : "warning",
                hasDocuments
                    ? $"{documents.Count} документа и {evaluations.Count} въпроса са готови."
                    : $"Няма локални документи. Налични са {evaluations.Count} benchmark въпроса.",
                null,
                timer.ElapsedMilliseconds,
                new Dictionary<string, string>
                {
                    ["Документи"] = documents.Count.ToString(),
                    ["Тестови въпроси"] = evaluations.Count.ToString(),
                    ["Формат"] = "MD, TXT, PDF, DOCX + JSON"
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            return FailedCheck("corpus", "Тестови данни", exception, timer);
        }
    }

    private async Task<DiagnosticCheck> DiagnoseOpenAiAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await _openAiClient.ValidateCredentialsAsync(cancellationToken);
            timer.Stop();
            return new DiagnosticCheck(
                "openai",
                "OpenAI API",
                "healthy",
                "API ключът и моделът за embeddings са достъпни.",
                null,
                timer.ElapsedMilliseconds,
                new Dictionary<string, string>
                {
                    ["Генератор"] = _settings.OpenAI.GeneratorModel,
                    ["Embedding модел"] = _settings.OpenAI.EmbeddingModel,
                    ["Размерност"] = _settings.OpenAI.EmbeddingDimensions.ToString(),
                    ["Източник"] = _settings.OpenAiCredentialSource
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            return FailedCheck("openai", "OpenAI API", exception, timer);
        }
    }

    private async Task<DiagnosticCheck> DiagnosePostgresAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var store = new PostgresSearchStore(_settings.Postgres);
            var health = await store.CheckHealthAsync(cancellationToken);
            if (!health.CanConnect)
            {
                throw new InvalidOperationException(health.Error ?? "PostgreSQL connection failed.");
            }

            var stats = await store.GetIndexStatsAsync(cancellationToken);
            timer.Stop();
            var state = health.VectorExtensionInstalled && stats.Available ? "healthy" : "warning";
            var summary = stats.Available
                ? $"Свързан; {stats.DocumentCount} документа и {stats.ChunkCount} части са индексирани."
                : "Свързан, но лабораторният индекс още не е готов.";

            return new DiagnosticCheck(
                "postgres",
                "PostgreSQL + pgvector",
                state,
                summary,
                stats.Error,
                timer.ElapsedMilliseconds,
                new Dictionary<string, string>
                {
                    ["pgvector"] = health.VectorExtensionInstalled ? "Инсталиран" : "Липсва",
                    ["Схема"] = _settings.Postgres.Schema,
                    ["Документи"] = stats.DocumentCount.ToString(),
                    ["Части"] = stats.ChunkCount.ToString(),
                    ["Източник"] = _settings.PostgresCredentialSource
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            return FailedCheck("postgres", "PostgreSQL + pgvector", exception, timer);
        }
    }

    private async Task<DiagnosticCheck> DiagnoseFileSearchAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var state = await FileSearchStateStore.LoadAsync(_settings.StatePath, cancellationToken);
            if (state is null)
            {
                timer.Stop();
                return new DiagnosticCheck(
                    "file-search",
                    "OpenAI File Search",
                    "warning",
                    "Няма локално записан Vector Store.",
                    "Създайте индекс от секцията Данни.",
                    timer.ElapsedMilliseconds,
                    new Dictionary<string, string>());
            }

            var status = await _openAiClient.GetVectorStoreStatusAsync(
                state.VectorStoreId,
                cancellationToken);
            timer.Stop();
            var healthy = string.Equals(status.Status, "completed", StringComparison.OrdinalIgnoreCase)
                && status.FailedFiles == 0;
            return new DiagnosticCheck(
                "file-search",
                "OpenAI File Search",
                healthy ? "healthy" : "warning",
                $"Vector Store е {status.Status}; {status.CompletedFiles}/{status.TotalFiles} файла са готови.",
                status.FailedFiles > 0 ? $"Неуспешни файлове: {status.FailedFiles}" : null,
                timer.ElapsedMilliseconds,
                new Dictionary<string, string>
                {
                    ["Vector Store"] = status.VectorStoreId,
                    ["Име"] = status.Name,
                    ["Файлове"] = $"{status.CompletedFiles}/{status.TotalFiles}",
                    ["Изтича"] = status.ExpiresAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "според активността"
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            return FailedCheck("file-search", "OpenAI File Search", exception, timer);
        }
    }

    private static DiagnosticCheck FailedCheck(
        string id,
        string name,
        Exception exception,
        Stopwatch timer) =>
        new(
            id,
            name,
            "error",
            "Проверката е неуспешна.",
            exception.Message,
            timer.ElapsedMilliseconds,
            new Dictionary<string, string>());

    private sealed record StagedRetrieval(
        RetrievalAugmentedPipeline Pipeline,
        RetrievedContext? Context,
        Exception? Error);

    private sealed class UnavailablePipeline(string name, string message) : ISearchPipeline
    {
        public string Name { get; } = name;

        public Task<PipelineResult> ExecuteAsync(
            string question,
            int topK,
            CancellationToken cancellationToken) =>
            Task.FromException<PipelineResult>(new InvalidOperationException(message));
    }
}
