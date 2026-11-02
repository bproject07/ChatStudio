using SearchQuality.Domain;
using SearchQuality.Chat.Observability;
using SearchQuality.OpenAI;

namespace SearchQuality.Chat;

/// <summary>Профил на потребител — лабораторен еквивалент на истинска идентичност.</summary>
public sealed record ChatUser(string Id, string Name);

/// <summary>Настройки на чата, четени от секцията "Chat" в appsettings.json.</summary>
public sealed record ChatSettings(
    int ContextTokenBudget,
    int TopK,
    string DefaultStrategy,
    string Schema,
    string Model,
    string Provider,
    int MaxOutputTokens,
    IReadOnlyList<ChatUser> Users)
{
    /// <summary>При този процент от бюджета UI показва предупреждение.</summary>
    public const double WarningThreshold = 0.85;

    /// <summary>При този процент разговорът се компактва автоматично.
    /// 100%: бюджетът е наша политика, не лимит на модела — прекрачването
    /// с един ход е безобидно, следващият ход компактира.</summary>
    public const double CompactThreshold = 1.0;
}

public sealed record ChatMessage(
    string Role,
    string Text,
    DateTimeOffset CreatedAt,
    string? AgentKey = null,
    string? Model = null,
    int? PromptVersion = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    IReadOnlyList<string>? Steps = null);

/// <summary>Текущият артефакт (програма, режим...) — версията, към която сочи стрелката.</summary>
public sealed record CurrentArtifact(
    string DocumentId,
    string Kind,
    string Title,
    string Content,
    DateTimeOffset UpdatedAt);

public sealed record ArtifactSummary(string Kind, string Title, DateTimeOffset UpdatedAt);

public sealed record ArtifactNote(string Note, DateTimeOffset CreatedAt);

/// <summary>Действащ запис в дневника (заменените с корекция не се връщат).</summary>
public sealed record ActivityEntry(
    long Id,
    string Kind,
    string Label,
    string? DataJson,
    string? Note,
    DateTimeOffset HappenedAt);

/// <summary>
/// Корекцията сочи ред, който не съществува, е чужд или вече е
/// анулиран/заменен. Отделният тип позволява tool слоят да върне безопасно
/// „не е записано“, без да маскира други SQL грешки.
/// </summary>
public sealed class InactiveActivityReplacementException(long replacesId)
    : InvalidOperationException(
        $"replaces_id={replacesId} не е активен запис на този потребител; " +
        "прочети отново дневника и използвай id на действащия ред.")
{
    public long ReplacesId { get; } = replacesId;
}

/// <summary>Ред от прогреса: агрегат по (седмица, какво, числово поле) — смята го SQL.</summary>
public sealed record ProgressRow(
    string Week,
    string Label,
    string Field,
    decimal Min,
    decimal Avg,
    decimal Max,
    long Count);

/// <summary>Откъс от архива, подаден на модела като контекст — точно това вижда LLM-ът.</summary>
public sealed record ContextSnippet(
    string DocumentId,
    string DocumentTitle,
    string ChunkId,
    string Text,
    double Score,
    string Strategy)
{
    public RelevanceAssessment? Relevance { get; init; }
}

/// <summary>Състоянието на токен-метъра: колко от контекстния бюджет е изразходван.</summary>
public sealed record ContextMeter(
    int UsedTokens,
    int BudgetTokens,
    double Percent,
    bool Warning,
    bool CompactImminent);

/// <summary>
/// Детерминистичният резултат от дневникова операция. Той не е част от
/// свободния текст на модела: идва от реално изпълнения инструмент и затова
/// UI каналите могат недвусмислено да покажат какво е (или не е) записано.
/// </summary>
public sealed record DiaryConfirmation(
    string Status,
    string Kind,
    string Label,
    long? Id = null,
    string? ProgramDay = null,
    string? DataJson = null,
    string? Note = null,
    string? Message = null);

public static class DiaryConfirmationStatus
{
    public const string Recorded = "recorded";
    public const string Corrected = "corrected";
    public const string Voided = "voided";
    public const string NotRecorded = "not-recorded";
    public const string Rejected = "rejected";
}

/// <summary>
/// Потвържденията на един ход. Отказите се пазят по логическа операция
/// (kind + label): успешен retry чисти само отказа на СЪЩАТА операция, а
/// несвързан успех не скрива чужд отказ. Отказ без конкретен label описва
/// последното невалидно извикване и всеки следващ успех го чисти.
/// </summary>
public sealed class DiaryConfirmationLog
{
    private const string GenericLabel = "Дневниковата операция";

    private readonly List<DiaryConfirmation> _entries = [];
    private readonly List<DiaryConfirmation> _failures = [];

    /// <summary>Информативен ред (заменен ordinal, safety бележка).</summary>
    public void Add(DiaryConfirmation entry) => _entries.Add(entry);

    public void ReportSuccess(DiaryConfirmation success)
    {
        _entries.Add(success);
        _failures.RemoveAll(failure => ClearedBy(failure, success));
    }

    public void ReportFailure(string? kind, string? label, string message)
    {
        var failure = new DiaryConfirmation(
            DiaryConfirmationStatus.Rejected,
            string.IsNullOrWhiteSpace(kind) ? "дневник" : kind,
            string.IsNullOrWhiteSpace(label) ? GenericLabel : label,
            Message: message);
        _failures.RemoveAll(existing => SameOperation(existing, failure));
        _failures.Add(failure);
    }

    public IReadOnlyList<DiaryConfirmation> Snapshot() => [.. _entries, .. _failures];

    private static bool SameOperation(DiaryConfirmation left, DiaryConfirmation right) =>
        string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);

    private static bool ClearedBy(DiaryConfirmation failure, DiaryConfirmation success) =>
        string.Equals(failure.Label, GenericLabel, StringComparison.Ordinal)
        || (success.Id is { } id
            && string.Equals(
                failure.Label,
                $"id {id}",
                StringComparison.OrdinalIgnoreCase))
        || SameOperation(failure, success);
}

public sealed record ChatTurnResponse(
    ChatMessage Answer,
    IReadOnlyList<ContextSnippet> Context,
    string ContextInput,
    TokenUsage Usage,
    ContextMeter Meter,
    ArchiveDescriptor? CompactedTo,
    string AgentKey,
    string Model,
    int PromptVersion)
{
    /// <summary>Видимите стъпки на хода: търсене в регистъра, handoff, finish.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>Системните бележки, добавени в разговора през този ход.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Проверими резултати от дневника, независими от LLM текста.</summary>
    public IReadOnlyList<DiaryConfirmation> DiaryConfirmations { get; init; } = [];

    /// <summary>Кой държи микрофона след хода (NULL = Асистентът).</summary>
    public string? ActiveAgent { get; init; }

    public string? ActiveAgentName { get; init; }
}

public sealed record ChatStateResponse(
    string UserId,
    IReadOnlyList<ChatMessage> Messages,
    string? SummarySeed,
    ContextMeter Meter,
    ChatSettings Settings,
    ChatRuntimeSettings Runtime,
    IReadOnlyList<ArchiveDescriptor> Archives,
    string GenerationSource)
{
    public string? ActiveAgent { get; init; }

    public string? ActiveAgentName { get; init; }
}

/// <summary>Настройките, които потребителят управлява от UI по време на работа.</summary>
public sealed record ChatRuntimeSettings(string Model, int ContextTokenBudget);

public sealed record UpdateSettingsRequest(string? Model, int? ContextTokenBudget);

public sealed record ArchiveDescriptor(
    string Id,
    string Title,
    string FileName,
    DateTimeOffset CreatedAt,
    int ContentLength);

public sealed record SendMessageRequest(string? UserId, string? Text, string? Strategy, int TopK);

/// <summary>
/// Клиентът за генериране на отговорите и резюметата. Отделен тип от embedding
/// клиента, за да не се бъркат в DI: търсенето в архива винаги остава директно
/// към OpenAI. Провайдърът се избира от Chat:Provider — "openai" говори
/// Responses API, а "litellm" минава през /chat/completions, защото бекендите
/// зад proxy-то не поддържат /responses. Моделът се подава при всяко извикване,
/// за да може да се сменя от UI без рестарт.
/// </summary>
public sealed class ChatGenerationClient(
    HttpClient httpClient,
    SearchQuality.OpenAI.OpenAiSettings baseSettings,
    bool useChatCompletions,
    string providerLabel,
    ChatTelemetry? telemetry = null)
{
    private readonly ChatTelemetry _telemetry = telemetry ?? ChatTelemetry.Disabled;

    private Task<T> TraceModelAsync<T>(string model, string instructions,
        IReadOnlyList<ChatTurnMessage> conversation, IReadOnlyList<ToolDefinition> tools,
        bool streaming, Func<Task<T>> action) =>
        _telemetry.TraceAsync("llm " + model, "LLM", async span =>
        {
            if (span.Activity is null) return await action();
            span.Tag("llm.model_name", model).Tag("chat.provider", providerLabel)
                .Tag("chat.streaming", streaming)
                .Messages("input", [new("system", instructions), .. conversation])
                .Json("input.value", new { instructions, conversation })
                .Tag("input.mime_type", "application/json")
                .Json("llm.invocation_parameters", new { model, max_tokens = baseSettings.MaxOutputTokens,
                    reasoning_effort = baseSettings.GeneratorReasoningEffort, stream = streaming });
            for (var i = 0; i < tools.Count; i++)
                span.Content($"llm.tools.{i}.tool.json_schema", System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "function", function = new { name = tools[i].Name, description = tools[i].Description,
                        parameters = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(tools[i].ParametersJsonSchema) }
                }));
            var result = await action();
            if (result is GeneratedAnswer answer)
                span.Output(answer.Text).Usage(answer.Usage).Messages("output", [new("assistant", answer.Text)]);
            else if (result is ChatToolResult toolResult)
                span.Output(toolResult.Content).Usage(toolResult.Usage)
                    .Messages("output", [new("assistant", toolResult.Content, toolResult.ToolCalls)])
                    .Tag("chat.tool_call.count", toolResult.ToolCalls.Count);
            return result;
        });
    public string SourceFor(string model) => $"{providerLabel} ({model})";

    public Task<SearchQuality.Domain.GeneratedAnswer> GenerateAsync(
        string model,
        string instructions,
        string input,
        CancellationToken cancellationToken)
    {
        var client = new SearchQuality.OpenAI.OpenAiApiClient(
            httpClient,
            baseSettings with { GeneratorModel = model });
        return TraceModelAsync(model, instructions, [new("user", input)], [], false, () => useChatCompletions
            ? client.GenerateChatCompletionAsync(instructions, input, cancellationToken)
            : client.GenerateTextAsync(instructions, input, cancellationToken));
    }

    /// <summary>
    /// Поточно генериране: onDelta получава всяко парче текст. При провайдър
    /// без реален streaming (OpenAI Responses пътя) отговорът се емулира с
    /// едно парче, за да работи UI еднакво и в двата режима.
    /// </summary>
    public async Task<SearchQuality.Domain.GeneratedAnswer> GenerateStreamingAsync(
        string model,
        string instructions,
        string input,
        Func<string, Task> onDelta,
        CancellationToken cancellationToken)
    {
        var client = new SearchQuality.OpenAI.OpenAiApiClient(
            httpClient,
            baseSettings with { GeneratorModel = model });
        if (useChatCompletions)
        {
            return await TraceModelAsync(model, instructions, [new("user", input)], [], true, () => client.GenerateChatCompletionStreamingAsync(
                instructions,
                input,
                onDelta,
                cancellationToken));
        }

        var answer = await TraceModelAsync(model, instructions, [new("user", input)], [], false,
            () => client.GenerateTextAsync(instructions, input, cancellationToken));
        await onDelta(answer.Text);
        return answer;
    }

    /// <summary>
    /// Ход с инструменти (function calling) — винаги през /chat/completions,
    /// който и двата провайдъра поддържат. Примката е у извикващия.
    /// </summary>
    public Task<SearchQuality.OpenAI.ChatToolResult> GenerateWithToolsAsync(
        string model,
        string instructions,
        IReadOnlyList<SearchQuality.OpenAI.ChatTurnMessage> conversation,
        IReadOnlyList<SearchQuality.OpenAI.ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var client = new SearchQuality.OpenAI.OpenAiApiClient(
            httpClient,
            baseSettings with { GeneratorModel = model });
        return TraceModelAsync(model, instructions, conversation, tools, false,
            () => client.GenerateChatCompletionWithToolsAsync(instructions, conversation, tools, cancellationToken));
    }

    /// <summary>
    /// Поточен ход с инструменти: решенията (tool calls) остават невидими,
    /// финалният текст тече към onDelta, докато се генерира.
    /// </summary>
    public Task<SearchQuality.OpenAI.ChatToolResult> GenerateWithToolsStreamingAsync(
        string model,
        string instructions,
        IReadOnlyList<SearchQuality.OpenAI.ChatTurnMessage> conversation,
        IReadOnlyList<SearchQuality.OpenAI.ToolDefinition> tools,
        Func<string, Task> onDelta,
        CancellationToken cancellationToken)
    {
        var client = new SearchQuality.OpenAI.OpenAiApiClient(
            httpClient,
            baseSettings with { GeneratorModel = model });
        return TraceModelAsync(model, instructions, conversation, tools, true,
            () => client.GenerateChatCompletionWithToolsStreamingAsync(
                instructions, conversation, tools, onDelta, cancellationToken));
    }

    /// <summary>
    /// Наличните модели от провайдъра (GET /models). LiteLLM връща точно
    /// моделите, позволени за ключа — идеално за dropdown менюто.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var baseUri = new Uri(baseSettings.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "models"));
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", baseSettings.ApiKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed record NewChatRequest(string? UserId, bool Archive);

public sealed record CompactRequest(string? UserId);

/// <summary>Живите бройки за диаграмата „Как работи“ на страницата История.</summary>
public sealed record HistoryStats(
    int Sessions,
    int Messages,
    int Documents,
    int Chunks,
    long TotalTokens);

/// <summary>Ред в списъка със сесии на страницата История.</summary>
public sealed record SessionSummary(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompactedAt,
    string? ArchiveDocumentId,
    int MessageCount,
    string Preview,
    int TotalTokens);

/// <summary>Реплика от суровия слой с целия ѝ произход — за страницата История.</summary>
public sealed record HistoryMessage(
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    string? AgentKey,
    string? Model,
    int? PromptVersion,
    string? ContextRefsJson,
    int? InputTokens,
    int? OutputTokens,
    string? StepsJson);

/// <summary>Възстановено състояние на активна (незатворена) сесия от базата.</summary>
public sealed record ActiveSessionState(
    Guid SessionId,
    string? SummarySeed,
    IReadOnlyList<ChatMessage> Messages,
    int LastContextTokens,
    string? ActiveAgent);

/// <summary>Агент от таблицата agents — за списъка в UI.</summary>
public sealed record AgentInfo(
    string Key,
    string DisplayName,
    string Kind,
    bool Enabled,
    string Provider,
    string Model,
    int? ActiveVersion,
    string Activation);

/// <summary>Версия на промпт — редовете са неизменими, сменя се само коя е активна.</summary>
public sealed record PromptVersionInfo(
    long Id,
    int Version,
    string Instructions,
    string? Note,
    DateTimeOffset CreatedAt,
    bool Active);

/// <summary>Каквото трябва на едно извикване: модел + активният промпт на агента.</summary>
public sealed record AgentRuntime(
    string Key,
    string DisplayName,
    string Provider,
    string Model,
    string Instructions,
    int PromptVersion);

public sealed record CreateVersionRequest(string? Instructions, string? Note, bool Activate);

public sealed record ActivateVersionRequest(int Version);

public sealed record UpdateAgentRequest(string? Model, bool? Enabled, string? Activation);

public sealed record CreateAgentRequest(
    string? Key,
    string? DisplayName,
    string? Activation,
    string? Model,
    string? Instructions);

public sealed record HandbackRequest(string? UserId);

/// <summary>
/// Избира генеративен клиент по provider на агента: "litellm" или "openai".
/// Липсващ provider пада към дефолтния — агент никога не остава без клиент.
/// </summary>
public sealed class GenerationClientResolver(
    IReadOnlyDictionary<string, ChatGenerationClient> clients,
    string defaultProvider)
{
    public ChatGenerationClient Resolve(string provider) =>
        clients.TryGetValue(provider, out var client)
            ? client
            : clients[defaultProvider];

    public string SourceFor(string provider, string model) =>
        $"{(clients.ContainsKey(provider) ? provider : defaultProvider)} ({model})";
}
