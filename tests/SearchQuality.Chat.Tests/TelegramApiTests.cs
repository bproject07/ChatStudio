using System.Net;
using System.Text;
using SearchQuality.Chat.Telegram;

namespace SearchQuality.Chat.Tests;

public sealed class TelegramApiTests
{
    [Fact]
    public async Task Rate_limit_waits_and_retries_once()
    {
        var handler = new SequenceHandler(
            """{"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":0}}""",
            """{"ok":true,"result":{"message_id":77}}""");
        var api = new TelegramApi(new HttpClient(handler), "test-token");

        var messageId = await api.SendMessageAsync(
            123,
            "hello",
            html: false,
            replyMarkup: null,
            CancellationToken.None);

        Assert.Equal(77, messageId);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Second_rate_limit_exposes_structured_error()
    {
        var handler = new SequenceHandler(
            """{"ok":false,"error_code":429,"description":"Too Many Requests","parameters":{"retry_after":0}}""",
            """{"ok":false,"error_code":429,"description":"Still limited","parameters":{"retry_after":3}}""");
        var api = new TelegramApi(new HttpClient(handler), "test-token");

        var exception = await Assert.ThrowsAsync<TelegramApiException>(() =>
            api.SendMessageAsync(
                123,
                "hello",
                html: false,
                replyMarkup: null,
                CancellationToken.None));

        Assert.Equal(429, exception.ErrorCode);
        Assert.Equal(3, exception.RetryAfter);
        Assert.Equal(2, handler.CallCount);
    }

    private sealed class SequenceHandler(params string[] responses) : HttpMessageHandler
    {
        private int index;

        public int CallCount => index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responses[Math.Min(index, responses.Length - 1)];
            index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
