using System.Text;
using System.Text.Json;

namespace SearchQuality.Chat.Telegram;

/// <summary>
/// Тънка обвивка над Telegram Bot API — само методите, които ботът ползва.
/// Никаква логика: праща заявка, връща резултата, превежда грешките на
/// изключения. Всичко умно живее в <see cref="TelegramBotService"/>.
/// </summary>
public sealed class TelegramApi(HttpClient httpClient, string botToken)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Дълъг poll за нови събития. Връща суровите update обекти.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetUpdatesAsync(
        long offset, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var document = await CallAsync(
            "getUpdates",
            new { offset, timeout = timeoutSeconds, allowed_updates = new[] { "message", "callback_query" } },
            cancellationToken);
        return document.RootElement.GetProperty("result")
            .EnumerateArray()
            .Select(update => update.Clone())
            .ToList();
    }

    /// <summary>Праща съобщение; връща message_id (нужен за последващи редакции).</summary>
    public async Task<long> SendMessageAsync(
        long chatId, string text, bool html, object? replyMarkup, CancellationToken cancellationToken)
    {
        using var document = await CallAsync(
            "sendMessage",
            new
            {
                chat_id = chatId,
                text,
                parse_mode = html ? "HTML" : null,
                reply_markup = replyMarkup,
                disable_web_page_preview = true
            },
            cancellationToken);
        return document.RootElement.GetProperty("result").GetProperty("message_id").GetInt64();
    }

    public async Task EditMessageTextAsync(
        long chatId, long messageId, string text, bool html, CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await CallAsync(
                "editMessageText",
                new
                {
                    chat_id = chatId,
                    message_id = messageId,
                    text,
                    parse_mode = html ? "HTML" : null,
                    disable_web_page_preview = true
                },
                cancellationToken);
        }
        catch (TelegramApiException exception)
            when (exception.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Редакция със същия текст — безобидно, случва се при тих интервал.
        }
    }

    public async Task SendChatActionAsync(long chatId, CancellationToken cancellationToken)
    {
        using var _ = await CallAsync(
            "sendChatAction", new { chat_id = chatId, action = "typing" }, cancellationToken);
    }

    public async Task AnswerCallbackQueryAsync(string callbackQueryId, CancellationToken cancellationToken)
    {
        using var _ = await CallAsync(
            "answerCallbackQuery", new { callback_query_id = callbackQueryId }, cancellationToken);
    }

    private async Task<JsonDocument> CallAsync(
        string method, object payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"https://api.telegram.org/bot{botToken}/{method}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw new TelegramApiException(
                    $"Telegram {method}: невалиден отговор от услугата.",
                    innerException: exception);
            }

            if (document.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True)
            {
                return document;
            }

            var errorCode = document.RootElement.TryGetProperty(
                "error_code",
                out var errorCodeElement)
                && errorCodeElement.TryGetInt32(out var parsedErrorCode)
                    ? parsedErrorCode
                    : (int?)null;
            var retryAfter = document.RootElement.TryGetProperty(
                "parameters",
                out var parameters)
                && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("retry_after", out var retryElement)
                && retryElement.TryGetInt32(out var parsedRetryAfter)
                    ? parsedRetryAfter
                    : (int?)null;
            var description = document.RootElement.TryGetProperty(
                "description",
                out var descriptionElement)
                && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString() ?? "неизвестна грешка"
                : "неизвестна грешка";
            document.Dispose();

            if (errorCode == 429 && retryAfter is { } seconds && attempt == 0)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60)),
                    cancellationToken);
                continue;
            }

            throw new TelegramApiException(
                $"Telegram {method}: {description}",
                errorCode,
                retryAfter);
        }

        throw new TelegramApiException($"Telegram {method}: заявката не беше изпълнена.");
    }
}

public sealed class TelegramApiException(
    string message,
    int? errorCode = null,
    int? retryAfter = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public int? ErrorCode { get; } = errorCode;

    public int? RetryAfter { get; } = retryAfter;
}
