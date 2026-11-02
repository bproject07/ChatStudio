using System.Net;
using System.Text.Json;
using SearchQuality.Domain;
using SearchQuality.OpenAI;

namespace SearchQuality.Tests.OpenAI;

public sealed class OpenAiApiClientTests
{
    [Fact]
    public async Task CreateEmbeddings_sends_model_dimensions_and_inputs()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {"data":[{"index":0,"embedding":[0.1,0.2,0.3]}],"usage":{"prompt_tokens":4,"total_tokens":4}}
            """));
        var client = CreateClient(handler);

        var result = await client.CreateEmbeddingsAsync(["hello"], CancellationToken.None);

        var vector = Assert.Single(result.Vectors);
        Assert.Equal(3, vector.Length);
        Assert.Equal(4, result.Usage.InputTokens);
        Assert.Equal(4, result.Usage.TotalTokens);
        Assert.Equal("/v1/embeddings", handler.Path);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("text-embedding-3-large", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(3, payload.RootElement.GetProperty("dimensions").GetInt32());
    }

    [Fact]
    public async Task JudgeRelevance_parses_structured_scores_and_uses_judge_model()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "output": [{
                "type": "message",
                "content": [{
                  "type": "output_text",
                  "text": "{\"assessments\":[{\"candidate_id\":1,\"score\":0,\"reason\":\"Няма данни за отпуск.\"},{\"candidate_id\":2,\"score\":94,\"reason\":\"Дава точния брой дни.\"}]}"
                }]
              }],
              "usage": {"input_tokens":120,"output_tokens":40,"total_tokens":160}
            }
            """));
        var client = CreateClient(handler);
        var candidates = new[]
        {
            new SearchHit("offer", "Offer", "offer-1", "Софтуерна оферта", 0.31),
            new SearchHit("leave", "Leave", "leave-1", "Служителят има 25 дни отпуск", 0.61)
        };

        var result = await client.JudgeRelevanceAsync(
            "Колко дни отпуск има служителят?",
            candidates,
            CancellationToken.None);

        Assert.Equal(2, result.Judgments.Count);
        Assert.Equal(0, result.Judgments[0].Score);
        Assert.Equal(0.94, result.Judgments[1].Score, precision: 12);
        Assert.Equal(160, result.Usage.TotalTokens);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("gpt-5.5", payload.RootElement.GetProperty("model").GetString());
        var format = payload.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task SearchFilesAndAnswer_parses_hits_answer_and_usage()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "output": [
                {
                  "type": "file_search_call",
                  "results": [
                    {"file_id":"file_1","filename":"annual-leave-policy.md","score":0.93,"text":"25 работни дни"}
                  ]
                },
                {
                  "type": "message",
                  "content": [{"type":"output_text","text":"Отпускът е 25 дни."}]
                }
              ],
              "usage": {"input_tokens":100,"output_tokens":20,"total_tokens":120}
            }
            """));
        var client = CreateClient(handler);

        var result = await client.SearchFilesAndAnswerAsync("vs_123", "Колко дни?", 5, CancellationToken.None);

        Assert.Equal("openai-file-search", result.Strategy);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("annual-leave-policy", hit.DocumentId);
        Assert.Equal(0.93, hit.Score, precision: 12);
        Assert.Equal("Отпускът е 25 дни.", result.Answer.Text);
        Assert.Equal(120, result.Answer.Usage.TotalTokens);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Contains(
            payload.RootElement.GetProperty("include").EnumerateArray(),
            value => value.GetString() == "file_search_call.results");
    }

    [Fact]
    public async Task Provider_error_does_not_expose_response_body_or_api_key()
    {
        const string sensitiveBody = "provider echoed private content";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(sensitiveBody)
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.CreateEmbeddingsAsync(["hello"], CancellationToken.None));

        Assert.Contains("HTTP 400", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveBody, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_completion_sends_medium_reasoning_to_codex_raw()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {"choices":[{"message":{"content":"ГОТОВО"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}
            """));
        var client = CreateClient(handler, "gpt-5.4-mini", "medium");

        await client.GenerateChatCompletionAsync(
            "Инструкции",
            "Въпрос",
            CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(
            "medium",
            payload.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(payload.RootElement.TryGetProperty("allowed_openai_params", out _));
    }

    [Fact]
    public async Task Chat_completion_allows_reasoning_passthrough_for_matching_agy_effort()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {"choices":[{"message":{"content":"ГОТОВО"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}
            """));
        var client = CreateClient(handler, "agy-gemini-3.6-flash-medium", "medium");

        await client.GenerateChatCompletionAsync(
            "Инструкции",
            "Въпрос",
            CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(
            "medium",
            payload.RootElement.GetProperty("reasoning_effort").GetString());
        var allowed = payload.RootElement.GetProperty("allowed_openai_params");
        Assert.Equal("reasoning_effort", Assert.Single(allowed.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Chat_completion_omits_conflicting_agy_effort()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {"choices":[{"message":{"content":"ГОТОВО"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}
            """));
        var client = CreateClient(handler, "agy-gemini-3.6-flash-high", "medium");

        await client.GenerateChatCompletionAsync(
            "Инструкции",
            "Въпрос",
            CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.False(payload.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(payload.RootElement.TryGetProperty("allowed_openai_params", out _));
    }

    [Fact]
    public async Task CreateFileSearchIndex_UploadsNormalizedMarkdownForEverySourceFormat()
    {
        var handler = new RecordingHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/v1/vector_stores" => JsonResponse("""{"id":"vs_test"}"""),
            "/v1/files" => JsonResponse("""{"id":"file_test"}"""),
            "/v1/vector_stores/vs_test/files" => JsonResponse("""{"id":"vs_file_test"}"""),
            "/v1/vector_stores/vs_test/files/file_test" => JsonResponse("""{"status":"completed"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var client = CreateClient(handler);
        var source = new SourceDocument(
            "policy",
            "Policy",
            "policy.pdf",
            "Normalized searchable text");

        var state = await client.CreateFileSearchIndexAsync(
            "test-store",
            [source],
            new Dictionary<string, string> { ["policy"] = "hash-policy" },
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("vs_test", state.VectorStoreId);
        Assert.NotNull(state.Files);
        Assert.Equal("hash-policy", state.Files["policy"].ContentHash);
        Assert.Equal("file_test", state.Files["policy"].FileId);
        var upload = Assert.Single(handler.Requests, request => request.Path == "/v1/files");
        Assert.Contains("policy.md", upload.Body, StringComparison.Ordinal);
        Assert.Contains("Normalized searchable text", upload.Body, StringComparison.Ordinal);
    }

    private static OpenAiApiClient CreateClient(
        HttpMessageHandler handler,
        string generatorModel = "gpt-5.4-mini",
        string reasoningEffort = "low") =>
        new(
            new HttpClient(handler),
            new OpenAiSettings(
                "test-key",
                "https://api.example.test/v1",
                generatorModel,
                reasoningEffort,
                "gpt-5.5",
                "text-embedding-3-large",
                3,
                500,
                800,
                400));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(Path, Body));
            return responseFactory(request);
        }
    }

    private sealed record RecordedRequest(string? Path, string? Body);
}
