using System.Net;
using SearchQuality.Domain;
using SearchQuality.OpenAI;

namespace SearchQuality.Tests.OpenAI;

public sealed class OpenAiApiClientResilienceTests
{
    [Fact]
    public async Task Transient_status_is_retried_and_then_succeeds()
    {
        var attempts = 0;
        var handler = new SequenceHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) }
                }
                : JsonResponse(
                    """
                    {"data":[{"index":0,"embedding":[0.1,0.2,0.3]}],"usage":{"prompt_tokens":4,"total_tokens":4}}
                    """);
        });
        var client = CreateClient(handler);

        var result = await client.CreateEmbeddingsAsync(["hello"], TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        Assert.Single(result.Vectors);
    }

    [Fact]
    public async Task Non_transient_error_is_not_retried_and_surfaces_structured_message()
    {
        var attempts = 0;
        var handler = new SequenceHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"Unknown parameter: 'description'."}}""")
            };
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.CreateEmbeddingsAsync(["hello"], TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Contains("HTTP 400", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Unknown parameter", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VectorStoreStatus_tolerates_null_expiry_and_null_counts()
    {
        var handler = new SequenceHandler(_ => JsonResponse(
            """
            {
              "id": "vs_1",
              "name": "lab",
              "status": "completed",
              "file_counts": {"completed": 2, "total": null, "failed": 0},
              "expires_at": null
            }
            """));
        var client = CreateClient(handler);

        var status = await client.GetVectorStoreStatusAsync("vs_1", TestContext.Current.CancellationToken);

        Assert.Null(status.ExpiresAt);
        Assert.Equal(2, status.CompletedFiles);
        Assert.Equal(0, status.TotalFiles);
    }

    [Fact]
    public async Task Incomplete_response_throws_clear_error_instead_of_missing_text()
    {
        var handler = new SequenceHandler(_ => JsonResponse(
            """
            {
              "status": "incomplete",
              "incomplete_details": {"reason": "max_output_tokens"},
              "output": []
            }
            """));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.GenerateAsync(
                "Въпрос?",
                [new SearchHit("doc", "Doc", "chunk", "текст", 0.5)],
                TestContext.Current.CancellationToken));

        Assert.Contains("incomplete", exception.Message, StringComparison.Ordinal);
        Assert.Contains("max_output_tokens", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Large_input_lists_are_split_into_embedding_batches()
    {
        var requestCounts = new List<int>();
        var handler = new SequenceHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var payload = System.Text.Json.JsonDocument.Parse(body);
            var count = payload.RootElement.GetProperty("input").GetArrayLength();
            requestCounts.Add(count);
            var data = string.Join(',', Enumerable.Range(0, count)
                .Select(index => $$"""{"index":{{index}},"embedding":[0.1,0.2,0.3]}"""));
            return JsonResponse(
                $$"""{"data":[{{data}}],"usage":{"prompt_tokens":{{count}},"total_tokens":{{count}} } }""");
        });
        var client = CreateClient(handler);
        var inputs = Enumerable.Range(0, 200).Select(index => $"chunk {index}").ToArray();

        var result = await client.CreateEmbeddingsAsync(inputs, TestContext.Current.CancellationToken);

        Assert.Equal(200, result.Vectors.Count);
        Assert.Equal([128, 72], requestCounts);
        Assert.Equal(200, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task Streaming_chat_completion_collects_deltas_and_usage()
    {
        const string sse = """
            data: {"choices":[{"delta":{"role":"assistant","content":""}}]}

            data: {"choices":[{"delta":{"content":"Здра"}}]}

            data: {"choices":[{"delta":{"content":"вей!"}}]}

            data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}

            data: [DONE]

            """;
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
        });
        var client = CreateClient(handler);
        var deltas = new List<string>();

        var answer = await client.GenerateChatCompletionStreamingAsync(
            "инструкции",
            "вход",
            delta => { deltas.Add(delta); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(["Здра", "вей!"], deltas);
        Assert.Equal("Здравей!", answer.Text);
        Assert.Equal(10, answer.Usage.InputTokens);
        Assert.Equal(5, answer.Usage.OutputTokens);
        Assert.Equal(15, answer.Usage.TotalTokens);
    }

    [Fact]
    public async Task Streaming_chat_completion_rejects_eof_without_completion_marker()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Частичен отговор"}}]}

            """;
        var client = CreateClient(new SequenceHandler(_ => SseResponse(sse)));

        var exception = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.GenerateChatCompletionStreamingAsync(
                "инструкции",
                "вход",
                _ => Task.CompletedTask,
                TestContext.Current.CancellationToken));

        Assert.Contains("completion marker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_chat_completion_accepts_finish_reason_without_done_marker()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Завършен отговор"},"finish_reason":"stop"}]}

            """;
        var client = CreateClient(new SequenceHandler(_ => SseResponse(sse)));

        var answer = await client.GenerateChatCompletionStreamingAsync(
            "инструкции",
            "вход",
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Equal("Завършен отговор", answer.Text);
    }

    [Fact]
    public async Task Chat_completion_rejects_output_stopped_at_token_limit()
    {
        var client = CreateClient(new SequenceHandler(_ => JsonResponse(
            """
            {"choices":[{"message":{"content":"Частичен отговор"},"finish_reason":"length"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
            """)));

        var exception = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.GenerateChatCompletionAsync(
                "инструкции",
                "вход",
                TestContext.Current.CancellationToken));

        Assert.Contains("output token limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_chat_completion_rejects_output_stopped_at_token_limit()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Частичен отговор"},"finish_reason":"length"}]}

            data: [DONE]

            """;
        var client = CreateClient(new SequenceHandler(_ => SseResponse(sse)));

        var exception = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.GenerateChatCompletionStreamingAsync(
                "инструкции",
                "вход",
                _ => Task.CompletedTask,
                TestContext.Current.CancellationToken));

        Assert.Contains("output token limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_calls_are_parsed_from_structured_field_and_inline_fallback()
    {
        // Първи отговор: структурирано tool_calls поле. Втори: inline формат.
        var responses = new Queue<string>([
            """
            {"choices":[{"message":{"content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"find_specialists","arguments":"{\"need\":\"тренировка\"}"}}]}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
            """,
            """
            {"choices":[{"message":{"content":"Ще проверя.<tool_call>{\"name\":\"handoff_to\",\"arguments\":{\"agent_key\":\"coach\",\"task\":\"програма\"}}</tool_call>"}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
            """
        ]);
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses.Dequeue(), System.Text.Encoding.UTF8, "application/json")
        });
        var client = CreateClient(handler);
        var tools = new[] { new ToolDefinition("t", "d", """{"type":"object","properties":{}}""") };

        var structured = await client.GenerateChatCompletionWithToolsAsync(
            "и", [new ChatTurnMessage("user", "въпрос")], tools, TestContext.Current.CancellationToken);
        Assert.Single(structured.ToolCalls);
        Assert.Equal("find_specialists", structured.ToolCalls[0].Name);

        var inline = await client.GenerateChatCompletionWithToolsAsync(
            "и", [new ChatTurnMessage("user", "въпрос")], tools, TestContext.Current.CancellationToken);
        Assert.Single(inline.ToolCalls);
        Assert.Equal("handoff_to", inline.ToolCalls[0].Name);
        Assert.Contains("coach", inline.ToolCalls[0].ArgumentsJson, StringComparison.Ordinal);
        Assert.Equal("Ще проверя.", inline.Content);
    }

    [Fact]
    public async Task Plain_assistant_messages_with_null_content_are_serialized_as_empty_text()
    {
        var handler = new SequenceHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
            using var payload = System.Text.Json.JsonDocument.Parse(body);
            var assistant = payload.RootElement
                .GetProperty("messages")
                .EnumerateArray()
                .Single(message =>
                    message.GetProperty("role").GetString() == "assistant");

            Assert.Equal(
                string.Empty,
                assistant.GetProperty("content").GetString());
            return JsonResponse(
                """
                {"choices":[{"message":{"content":"Готово."}}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}
                """);
        });
        var client = CreateClient(handler);
        var tools = new[]
        {
            new ToolDefinition("t", "d", """{"type":"object","properties":{}}""")
        };

        var result = await client.GenerateChatCompletionWithToolsAsync(
            "инструкции",
            [
                new ChatTurnMessage("user", "първи въпрос"),
                new ChatTurnMessage("assistant", null),
                new ChatTurnMessage("user", "корекция")
            ],
            tools,
            TestContext.Current.CancellationToken);

        Assert.Equal("Готово.", result.Content);
    }

    [Fact]
    public async Task Inline_activity_tool_call_never_leaks_as_final_text()
    {
        var client = CreateClient(new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"choices":[{"message":{"content":"Ще запиша.<tool_call>{\"name\":\"log_activity\",\"arguments\":{\"kind\":\"упражнение\",\"label\":\"Landmine press\"}}</tool_call>"}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
                """,
                System.Text.Encoding.UTF8,
                "application/json")
        }));
        var tools = new[] { new ToolDefinition("log_activity", "d", """{"type":"object","properties":{}}""") };

        var result = await client.GenerateChatCompletionWithToolsAsync(
            "и",
            [new ChatTurnMessage("user", "Landmine press е готово.")],
            tools,
            TestContext.Current.CancellationToken);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("log_activity", call.Name);
        Assert.Contains("Landmine press", call.ArgumentsJson, StringComparison.Ordinal);
        Assert.Equal("Ще запиша.", result.Content);
        Assert.DoesNotContain("<tool_call", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicated_inline_markup_is_removed_when_structured_tool_call_exists()
    {
        var client = CreateClient(new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"choices":[{"message":{"content":"<tool_call>{\"name\":\"log_activity\",\"arguments\":{\"kind\":\"упражнение\",\"label\":\"Landmine press\"}}</tool_call>","tool_calls":[{"id":"c1","type":"function","function":{"name":"log_activity","arguments":"{\"kind\":\"упражнение\",\"label\":\"Landmine press\"}"}}]}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
                """,
                System.Text.Encoding.UTF8,
                "application/json")
        }));
        var tools = new[] { new ToolDefinition("log_activity", "d", """{"type":"object","properties":{}}""") };

        var result = await client.GenerateChatCompletionWithToolsAsync(
            "и",
            [new ChatTurnMessage("user", "Landmine press е готово.")],
            tools,
            TestContext.Current.CancellationToken);

        Assert.Single(result.ToolCalls);
        Assert.Equal(string.Empty, result.Content);
    }

    [Theory]
    [InlineData("Готово.</tool_call>", "Готово.")]
    [InlineData("Готово.<tool_call", "Готово.")]
    public async Task Orphaned_or_dangling_tool_markup_is_removed(
        string content,
        string expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
            usage = new
            {
                prompt_tokens = 10,
                completion_tokens = 5,
                total_tokens = 15
            }
        });
        var client = CreateClient(new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                System.Text.Encoding.UTF8,
                "application/json")
        }));
        var tools = new[] { new ToolDefinition("t", "d", """{"type":"object","properties":{}}""") };

        var result = await client.GenerateChatCompletionWithToolsAsync(
            "и",
            [new ChatTurnMessage("user", "въпрос")],
            tools,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Content);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public async Task Streaming_with_tools_hides_decisions_and_streams_only_text()
    {
        var client = CreateClient(new SequenceHandler(_ => SseResponse(
            """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"find_specialists","arguments":""}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"need\":\"тренировка\"}"}}]}}]}

            data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}

            data: [DONE]

            """)));
        var tools = new[] { new ToolDefinition("t", "d", """{"type":"object","properties":{}}""") };
        var deltas = new List<string>();
        Func<string, Task> collect = piece => { deltas.Add(piece); return Task.CompletedTask; };

        // Решение (структуриран tool call): нищо не тече към потребителя.
        var decision = await client.GenerateChatCompletionWithToolsStreamingAsync(
            "и", [new ChatTurnMessage("user", "въпрос")], tools, collect, TestContext.Current.CancellationToken);
        Assert.Empty(deltas);
        Assert.Single(decision.ToolCalls);
        Assert.Equal("find_specialists", decision.ToolCalls[0].Name);
        Assert.Contains("тренировка", decision.ToolCalls[0].ArgumentsJson, StringComparison.Ordinal);
        Assert.Equal(10, decision.Usage.InputTokens);

        // Текст: тече на парчета и се връща цял.
        client = CreateClient(new SequenceHandler(_ => SseResponse(
            """
            data: {"choices":[{"delta":{"content":"Здра"}}]}

            data: {"choices":[{"delta":{"content":"вей! Как мога да помогна днес?"}}]}

            data: [DONE]

            """)));
        var text = await client.GenerateChatCompletionWithToolsStreamingAsync(
            "и", [new ChatTurnMessage("user", "въпрос")], tools, collect, TestContext.Current.CancellationToken);
        Assert.Equal("Здравей! Как мога да помогна днес?", string.Concat(deltas));
        Assert.Equal("Здравей! Как мога да помогна днес?", text.Content);
        Assert.Empty(text.ToolCalls);

        // Inline <tool_call> като текст: задържа се и се разчита като решение.
        deltas.Clear();
        client = CreateClient(new SequenceHandler(_ => SseResponse(
            """
            data: {"choices":[{"delta":{"content":"<tool_"}}]}

            data: {"choices":[{"delta":{"content":"call>{\"name\":\"handoff_to\",\"arguments\":{\"agent_key\":\"coach\",\"task\":\"програма\"}}</tool_call>"}}]}

            data: [DONE]

            """)));
        var inline = await client.GenerateChatCompletionWithToolsStreamingAsync(
            "и", [new ChatTurnMessage("user", "въпрос")], tools, collect, TestContext.Current.CancellationToken);
        Assert.Empty(deltas);
        Assert.Single(inline.ToolCalls);
        Assert.Equal("handoff_to", inline.ToolCalls[0].Name);
        Assert.Contains("coach", inline.ToolCalls[0].ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_with_tools_rejects_eof_without_completion_marker()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Частичен отговор"}}]}

            """;
        var client = CreateClient(new SequenceHandler(_ => SseResponse(sse)));
        var tools = new[]
        {
            new ToolDefinition("t", "d", """{"type":"object","properties":{}}""")
        };

        var exception = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.GenerateChatCompletionWithToolsStreamingAsync(
                "и",
                [new ChatTurnMessage("user", "въпрос")],
                tools,
                _ => Task.CompletedTask,
                TestContext.Current.CancellationToken));

        Assert.Contains("completion marker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_with_tools_accepts_finish_reason_without_done_marker()
    {
        const string sse = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"find_specialists","arguments":"{\"need\":\"тренировка\"}"}}]},"finish_reason":"tool_calls"}]}

            """;
        var client = CreateClient(new SequenceHandler(_ => SseResponse(sse)));
        var tools = new[]
        {
            new ToolDefinition("t", "d", """{"type":"object","properties":{}}""")
        };

        var result = await client.GenerateChatCompletionWithToolsStreamingAsync(
            "и",
            [new ChatTurnMessage("user", "въпрос")],
            tools,
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Single(result.ToolCalls);
        Assert.Equal("find_specialists", result.ToolCalls[0].Name);
        Assert.Contains("тренировка", result.ToolCalls[0].ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_completion_with_tools_rejects_output_stopped_at_token_limit()
    {
        var client = CreateClient(new SequenceHandler(_ => JsonResponse(
            """
            {"choices":[{"message":{"content":"Частичен отговор"},"finish_reason":"length"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
            """)));
        var tools = new[]
        {
            new ToolDefinition("t", "d", """{"type":"object","properties":{}}""")
        };

        var exception = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.GenerateChatCompletionWithToolsAsync(
                "и",
                [new ChatTurnMessage("user", "въпрос")],
                tools,
                TestContext.Current.CancellationToken));

        Assert.Contains("output token limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streaming_with_tools_rejects_output_stopped_at_token_limit()
    {
        const string sse = """
            data: {"choices":[{"delta":{"content":"Частичен отговор"},"finish_reason":"length"}]}

            data: [DONE]

            """;
        var client = CreateClient(new SequenceHandler(_ => SseResponse(sse)));
        var tools = new[]
        {
            new ToolDefinition("t", "d", """{"type":"object","properties":{}}""")
        };

        var exception = await Assert.ThrowsAsync<OpenAiApiException>(() =>
            client.GenerateChatCompletionWithToolsStreamingAsync(
                "и",
                [new ChatTurnMessage("user", "въпрос")],
                tools,
                _ => Task.CompletedTask,
                TestContext.Current.CancellationToken));

        Assert.Contains("output token limit", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<tool_", "call")]
    [InlineData("</tool_", "call>")]
    public async Task Streaming_never_emits_partial_or_orphaned_tool_markup(
        string markerStart,
        string markerEnd)
    {
        var client = CreateClient(new SequenceHandler(_ => SseResponse(
            $$$$"""
            data: {"choices":[{"delta":{"content":"Готово."}}]}

            data: {"choices":[{"delta":{"content":"{{{{markerStart}}}}"}}]}

            data: {"choices":[{"delta":{"content":"{{{{markerEnd}}}}"}}]}

            data: [DONE]

            """)));
        var tools = new[] { new ToolDefinition("t", "d", """{"type":"object","properties":{}}""") };
        var deltas = new List<string>();

        var result = await client.GenerateChatCompletionWithToolsStreamingAsync(
            "и",
            [new ChatTurnMessage("user", "въпрос")],
            tools,
            piece =>
            {
                deltas.Add(piece);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("Готово.", string.Concat(deltas));
        Assert.Equal("Готово.", result.Content);
        Assert.DoesNotContain("tool_call", string.Concat(deltas), StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage SseResponse(string sse) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
        };

    private static OpenAiApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new OpenAiSettings(
                "test-key",
                "https://api.example.test/v1",
                "gpt-5.4-mini",
                "low",
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

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responseFactory;

        public SequenceHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
            _responseFactory = request => Task.FromResult(responseFactory(request));

        public SequenceHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory) =>
            _responseFactory = responseFactory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _responseFactory(request);
    }
}
