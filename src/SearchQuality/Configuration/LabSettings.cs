using System.Text.Json;
using SearchQuality.OpenAI;
using SearchQuality.Postgres;

namespace SearchQuality.Configuration;

public sealed record BenchmarkSettings(
    int TopK,
    int ChunkSizeWords,
    int ChunkOverlapWords);

public sealed record LabSettings(
    string RootDirectory,
    OpenAiSettings OpenAI,
    PostgresSettings Postgres,
    BenchmarkSettings Benchmark,
    string OpenAiCredentialSource,
    string PostgresCredentialSource)
{
    public string DocumentsDirectory => Path.Combine(RootDirectory, "data", "documents");
    public string EvaluationsPath => Path.Combine(RootDirectory, "data", "evaluation", "questions.json");
    public string StatePath => Path.Combine(RootDirectory, "state", "file-search-index.json");
    public string ReportsDirectory => Path.Combine(RootDirectory, "reports");
}

public static class LabSettingsLoader
{
    public static LabSettings Load(string rootDirectory)
    {
        var mainPath = Path.Combine(rootDirectory, "appsettings.json");
        using var main = JsonDocument.Parse(File.ReadAllText(mainPath));
        var root = main.RootElement;

        var localPath = Path.Combine(rootDirectory, "appsettings.Local.json");
        using var local = TryReadJson(localPath, strict: true);

        var fitnessPath = FirstNonEmpty(
            TryGetString(local, "ExternalConfiguration", "FitnessAppSettings"),
            GetString(root, "ExternalConfiguration", "FitnessAppSettings"));
        using var fitness = TryReadJson(fitnessPath, strict: false);

        var environmentApiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SEARCHQUALITY_OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var localApiKey = TryGetString(local, "OpenAI", "ApiKey");
        var externalApiKey = TryGetString(fitness, "OpenAI", "ApiKey");
        var apiKey = FirstNonEmpty(environmentApiKey, localApiKey, externalApiKey) ?? string.Empty;
        var apiKeySource = !string.IsNullOrWhiteSpace(environmentApiKey)
            ? "environment"
            : !string.IsNullOrWhiteSpace(localApiKey)
                ? "appsettings.Local.json"
                : !string.IsNullOrWhiteSpace(externalApiKey)
                    ? "fitness appsettings.Local.json"
                    : "not configured";

        var environmentConnection = Environment.GetEnvironmentVariable("SEARCHQUALITY_POSTGRES_CONNECTION_STRING");
        var localConnection = TryGetString(local, "ConnectionStrings", "Postgres");
        var externalConnection = TryGetString(fitness, "ConnectionStrings", "Postgres");
        var connectionString = FirstNonEmpty(environmentConnection, localConnection, externalConnection) ?? string.Empty;
        var connectionSource = !string.IsNullOrWhiteSpace(environmentConnection)
            ? "environment"
            : !string.IsNullOrWhiteSpace(localConnection)
                ? "appsettings.Local.json"
                : !string.IsNullOrWhiteSpace(externalConnection)
                    ? "fitness appsettings.Local.json"
                    : "not configured";

        var localBaseUrl = TryGetString(local, "OpenAI", "BaseUrl");
        var externalBaseUrl = TryGetString(fitness, "OpenAI", "BaseUrl");
        var baseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SEARCHQUALITY_OPENAI_BASE_URL"),
            localBaseUrl,
            externalBaseUrl,
            GetString(root, "OpenAI", "BaseUrl"))
            ?? throw new InvalidOperationException("Missing configuration value: OpenAI:BaseUrl");

        var fileSearchChunkSize = TryGetInt(local, "Benchmark", "FileSearchChunkSizeTokens")
            ?? GetInt(root, "Benchmark", "FileSearchChunkSizeTokens");
        var fileSearchOverlap = TryGetInt(local, "Benchmark", "FileSearchChunkOverlapTokens")
            ?? GetInt(root, "Benchmark", "FileSearchChunkOverlapTokens");

        // Генеративен маршрут: OpenAI:GenerationProvider = "litellm" праща
        // САМО генерирането на отговори през LiteLLM (chat/completions —
        // Codex бекендите нямат /responses). Embeddings, File Search и
        // съдията винаги остават на основния OpenAI маршрут.
        string? generationBaseUrl = null;
        string? generationApiKey = null;
        var generationUsesChat = false;
        var generationProvider = FirstNonEmpty(
            TryGetString(local, "OpenAI", "GenerationProvider"),
            GetString(root, "OpenAI", "GenerationProvider"));
        if (string.Equals(generationProvider, "litellm", StringComparison.OrdinalIgnoreCase))
        {
            generationBaseUrl = FirstNonEmpty(
                Environment.GetEnvironmentVariable("SEARCHQUALITY_LITELLM_BASE_URL"),
                TryGetString(local, "LiteLLM", "BaseUrl"),
                TryGetString(fitness, "LiteLLM", "BaseUrl"));
            generationApiKey = FirstNonEmpty(
                Environment.GetEnvironmentVariable("SEARCHQUALITY_LITELLM_API_KEY"),
                TryGetString(local, "LiteLLM", "ApiKey"),
                TryGetString(fitness, "LiteLLM", "ApiKey"));
            generationUsesChat = !string.IsNullOrWhiteSpace(generationBaseUrl);
        }

        return new LabSettings(
            rootDirectory,
            new OpenAiSettings(
                apiKey,
                baseUrl,
                GetRequiredOverlayString(local, root, "OpenAI", "GeneratorModel"),
                FirstNonEmpty(TryGetString(local, "OpenAI", "GeneratorReasoningEffort"), GetString(root, "OpenAI", "GeneratorReasoningEffort")) ?? string.Empty,
                GetRequiredOverlayString(local, root, "OpenAI", "JudgeModel"),
                GetRequiredOverlayString(local, root, "OpenAI", "EmbeddingModel"),
                TryGetInt(local, "OpenAI", "EmbeddingDimensions") ?? GetInt(root, "OpenAI", "EmbeddingDimensions"),
                TryGetInt(local, "OpenAI", "MaxOutputTokens") ?? GetInt(root, "OpenAI", "MaxOutputTokens"),
                fileSearchChunkSize,
                fileSearchOverlap,
                generationBaseUrl,
                generationApiKey,
                generationUsesChat),
            new PostgresSettings(
                connectionString,
                GetRequiredOverlayString(local, root, "Postgres", "Schema")),
            new BenchmarkSettings(
                TryGetInt(local, "Benchmark", "TopK") ?? GetInt(root, "Benchmark", "TopK"),
                TryGetInt(local, "Benchmark", "ChunkSizeWords") ?? GetInt(root, "Benchmark", "ChunkSizeWords"),
                TryGetInt(local, "Benchmark", "ChunkOverlapWords") ?? GetInt(root, "Benchmark", "ChunkOverlapWords")),
            apiKeySource,
            connectionSource);
    }

    private static JsonDocument? TryReadJson(string? path, bool strict)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException) when (!strict)
        {
            return null;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Invalid JSON in '{path}': {exception.Message}", exception);
        }
    }

    private static string? TryGetString(JsonDocument? document, params string[] path) =>
        document is null ? null : GetString(document.RootElement, path);

    private static int? TryGetInt(JsonDocument? document, params string[] path)
    {
        if (document is null)
        {
            return null;
        }

        var current = document.RootElement;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string? GetString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string GetRequiredOverlayString(JsonDocument? local, JsonElement root, params string[] path) =>
        FirstNonEmpty(TryGetString(local, path), GetString(root, path))
        ?? throw new InvalidOperationException($"Missing configuration value: {string.Join(':', path)}");

    private static int GetInt(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                throw new InvalidOperationException($"Missing configuration value: {string.Join(':', path)}");
            }
        }

        return current.GetInt32();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
