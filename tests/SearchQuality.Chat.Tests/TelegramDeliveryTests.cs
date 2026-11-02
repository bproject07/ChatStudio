using SearchQuality.Chat.Telegram;

namespace SearchQuality.Chat.Tests;

public sealed class TelegramDeliveryTests
{
    [Fact]
    public void Http_400_content_rejection_allows_plain_fallback() =>
        Assert.True(TelegramBotService.IsContentRejection(
            new TelegramApiException("Telegram sendMessage: can't parse entities", errorCode: 400)));

    [Fact]
    public void Rate_limit_is_not_a_content_rejection() =>
        Assert.False(TelegramBotService.IsContentRejection(
            new TelegramApiException("Telegram sendMessage: Too Many Requests", errorCode: 429, retryAfter: 5)));

    [Fact]
    public void Transport_errors_never_trigger_a_resend()
    {
        // Timeout: оригиналът може вече да е доставен — преизпращане = дубликат.
        Assert.False(TelegramBotService.IsContentRejection(
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")));
        Assert.False(TelegramBotService.IsContentRejection(
            new HttpRequestException("connection reset")));
        Assert.False(TelegramBotService.IsContentRejection(
            new TelegramApiException("Telegram sendMessage: невалиден отговор от услугата.")));
    }
}
