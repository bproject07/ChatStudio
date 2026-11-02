using System.Text.Json;

namespace SearchQuality.Chat;

public static class ChatSettingsLoader
{
    public static ChatSettings Load(string root)
    {
        // Секцията "Chat" в appsettings.json е по избор — има разумни стойности
        // по подразбиране, за да е нула конфигурацията на тръгване.
        // Първият потребител е 'ivan', защото съществуващите архиви отпреди
        // мулти-потребителския режим се присвояват на него при миграцията.
        IReadOnlyList<ChatUser> defaultUsers =
        [
            new ChatUser("ivan", "Иван"),
            new ChatUser("maria", "Мария"),
            new ChatUser("test", "Тест")
        ];
        // MaxOutputTokens на чата е отделен от лабораторния (700): reasoning
        // моделите (azure gpt-5-mini) хапят от този бюджет за мислене, а
        // компакторът пише дълги резюмета — 700 не стига и отговорът излиза
        // празен. 15000 дава простор и на мисленето, и на дълги отговори.
        var defaults = new ChatSettings(
            64000, 4, "postgres-hybrid", "chat_lab", "gpt-5.4-mini", "litellm", 15000, defaultUsers);
        var settings = ApplyChatSettingsFile(Path.Combine(root, "appsettings.json"), defaults);
        return ApplyChatSettingsFile(Path.Combine(root, "appsettings.Local.json"), settings);
    }

    public static (string BaseUrl, string ApiKey)? LoadLiteLlm(string root)
    {
        // Ред на четене за всяко поле поотделно (като в LabSettingsLoader):
        // environment променливи → локален appsettings.Local.json → ExternalConfiguration:FitnessAppSettings.
        var localConfigPath = Path.Combine(root, "appsettings.Local.json");
        var fitnessPath = TryReadExternalFitnessPath(localConfigPath, strict: true)
            ?? TryReadExternalFitnessPath(Path.Combine(root, "appsettings.json"), strict: true);

        var baseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SEARCHQUALITY_LITELLM_BASE_URL"),
            TryReadLiteLlmField(localConfigPath, "BaseUrl", strict: true),
            TryReadLiteLlmField(fitnessPath, "BaseUrl", strict: false));
        var apiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SEARCHQUALITY_LITELLM_API_KEY"),
            TryReadLiteLlmField(localConfigPath, "ApiKey", strict: true),
            TryReadLiteLlmField(fitnessPath, "ApiKey", strict: false));

        return string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)
            ? null
            : (baseUrl, apiKey);
    }

    public static string? LoadTelegramBotToken(string root)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("SEARCHQUALITY_TELEGRAM_BOT_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        // appsettings.Local.json е в .gitignore — мястото за тайни в репото.
        var path = Path.Combine(root, "appsettings.Local.json");
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = ParseJsonFile(path, strict: true)!;
        return document.RootElement.TryGetProperty("Telegram", out var telegram)
            && telegram.ValueKind == JsonValueKind.Object
            && telegram.TryGetProperty("BotToken", out var token)
            && token.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(token.GetString())
                ? token.GetString()!.Trim()
                : null;
    }

    private static ChatSettings ApplyChatSettingsFile(string path, ChatSettings current)
    {
        if (!File.Exists(path))
        {
            return current;
        }

        using var document = ParseJsonFile(path, strict: true)!;
        if (!document.RootElement.TryGetProperty("Chat", out var chat)
            || chat.ValueKind != JsonValueKind.Object)
        {
            return current;
        }

        var users = current.Users;
        if (chat.TryGetProperty("Users", out var usersElement)
            && usersElement.ValueKind == JsonValueKind.Array)
        {
            var parsed = usersElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new ChatUser(
                    GetString(item, "Id", string.Empty),
                    GetString(item, "Name", string.Empty)))
                .Where(user => !string.IsNullOrWhiteSpace(user.Id) && !string.IsNullOrWhiteSpace(user.Name))
                .ToArray();
            if (parsed.Length > 0)
            {
                users = parsed;
            }
        }

        return new ChatSettings(
            GetInt(chat, "ContextTokenBudget", current.ContextTokenBudget),
            GetInt(chat, "TopK", current.TopK),
            GetString(chat, "DefaultStrategy", current.DefaultStrategy),
            GetString(chat, "Schema", current.Schema),
            GetString(chat, "Model", current.Model),
            GetString(chat, "Provider", current.Provider),
            GetInt(chat, "MaxOutputTokens", current.MaxOutputTokens),
            users);
    }

    private static JsonDocument? ParseJsonFile(string? path, bool strict)
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
        catch (IOException) when (!strict)
        {
            return null;
        }
    }

    private static string? TryReadExternalFitnessPath(string path, bool strict)
    {
        using var config = ParseJsonFile(path, strict);
        return config is not null
            && config.RootElement.TryGetProperty("ExternalConfiguration", out var external)
            && external.ValueKind == JsonValueKind.Object
            && external.TryGetProperty("FitnessAppSettings", out var fitnessPathElement)
            && fitnessPathElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(fitnessPathElement.GetString())
                ? fitnessPathElement.GetString()
                : null;
    }

    private static string? TryReadLiteLlmField(string? path, string fieldName, bool strict)
    {
        using var document = ParseJsonFile(path, strict);
        return document is not null
            && document.RootElement.TryGetProperty("LiteLLM", out var liteLlm)
            && liteLlm.ValueKind == JsonValueKind.Object
            && liteLlm.TryGetProperty(fieldName, out var field)
            && field.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(field.GetString())
                ? field.GetString()
                : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static int GetInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private static string GetString(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
}
