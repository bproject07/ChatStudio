namespace SearchQuality.OpenAI;

public sealed record OpenAiSettings(
    string ApiKey,
    string BaseUrl,
    string GeneratorModel,
    string GeneratorReasoningEffort,
    string JudgeModel,
    string EmbeddingModel,
    int EmbeddingDimensions,
    int MaxOutputTokens,
    int FileSearchChunkSizeTokens,
    int FileSearchChunkOverlapTokens,
    // Отделен маршрут САМО за генерирането на отговори (напр. LiteLLM →
    // Codex за gpt-5.5): embeddings, File Search и съдията остават на
    // основния BaseUrl. Null = всичко върви по основния (както досега).
    string? GenerationBaseUrl = null,
    string? GenerationApiKey = null,
    bool GenerationUsesChatCompletions = false)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public void Validate()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "OpenAI API key is not configured. Set OPENAI_API_KEY or SEARCHQUALITY_OPENAI_API_KEY.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("OpenAI BaseUrl must be an absolute URI.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(GeneratorModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(JudgeModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(EmbeddingModel);
        ArgumentOutOfRangeException.ThrowIfLessThan(EmbeddingDimensions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxOutputTokens, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(FileSearchChunkSizeTokens, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(FileSearchChunkOverlapTokens);

        if (FileSearchChunkOverlapTokens > FileSearchChunkSizeTokens / 2)
        {
            throw new InvalidOperationException(
                "File Search chunk overlap cannot exceed half of the chunk size.");
        }
    }
}

public sealed record FileSearchIndexState(
    string VectorStoreId,
    IReadOnlyList<string> FileIds,
    DateTimeOffset CreatedAt,
    // Карта документ → качен файл за инкрементално синхронизиране. Null при
    // state, записан преди тази възможност — тогава първият sync прави пълно
    // преизграждане и я попълва.
    IReadOnlyDictionary<string, FileSearchFileEntry>? Files = null);

public sealed record FileSearchFileEntry(
    string FileId,
    string ContentHash,
    DateTimeOffset UploadedAt);

public sealed record FileSearchStoreStatus(
    string VectorStoreId,
    string Name,
    string Status,
    int CompletedFiles,
    int TotalFiles,
    int FailedFiles,
    DateTimeOffset? ExpiresAt);

public sealed class OpenAiApiException(string message) : Exception(message);

/// <summary>Дефиниция на инструмент за function calling (JSON Schema за параметрите).</summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    string ParametersJsonSchema);

/// <summary>Поискано от модела извикване на инструмент.</summary>
public sealed record ToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

/// <summary>
/// Реплика в tool-разговор: обикновена (Role+Content), заявка за инструменти
/// (ToolCalls) или резултат от инструмент (ToolCallId+Content).
/// </summary>
public sealed record ChatTurnMessage(
    string Role,
    string? Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>Резултат от chat completion с инструменти.</summary>
public sealed record ChatToolResult(
    string? Content,
    IReadOnlyList<ToolCall> ToolCalls,
    Domain.TokenUsage Usage);
