using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using SearchQuality.Chat.Observability;
using SearchQuality.OpenAI;

namespace SearchQuality.Chat.Tests;

[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection("Telemetry")]
public sealed class ChatTelemetryTests
{
    [Theory]
    [InlineData("http://public.example/v1/traces")]
    [InlineData("https://user:pass@example.invalid/v1/traces")]
    [InlineData("https://example.invalid/v1/traces?token=value")]
    [InlineData("https://example.invalid/")]
    [InlineData("ftp://localhost/v1/traces")]
    public void Refuses_unsafe_collector_endpoints(string endpoint)
    {
        var options = Options() with { Endpoint = new Uri(endpoint) };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Requires_authentication_and_does_not_print_key()
    {
        Assert.Throws<InvalidOperationException>(() => (Options() with { ApiKey = "" }).Validate());
        var options = Options();
        options.Validate();
        Assert.DoesNotContain(options.ApiKey, options.ToString());
    }

    [Fact]
    public void Creates_a_real_trace_root_under_an_unexported_web_request_and_restores_the_request()
    {
        using var capture = new Capture();
        using var webRequest = new Activity("web-request").Start();
        var telemetry = new ChatTelemetry(Options());
        using (var root = telemetry.Start("chat.turn"))
        {
            Assert.Equal(default, root.Activity!.ParentSpanId);
            Assert.NotEqual(webRequest.TraceId, root.Activity.TraceId);
            Assert.Equal(webRequest.Context, Assert.Single(root.Activity.Links).Context);
            root.Complete();
        }
        Assert.Same(webRequest, Activity.Current);
    }

    [Fact]
    public async Task Parallel_sessions_keep_their_own_context_and_child_relationships()
    {
        using var capture = new Capture();
        var telemetry = new ChatTelemetry(Options());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => telemetry.TraceAsync("turn", "CHAIN", async root =>
        {
            root.Tag("session.id", "session-" + index).Tag("user.id", "user-" + index);
            await Task.Yield();
            return await telemetry.TraceAsync("tool", "TOOL", async child =>
            {
                await Task.Delay(5);
                child.Output("done");
                return index;
            });
        })));
        Assert.Equal(16, capture.Spans.Count);
        foreach (var child in capture.Spans.Where(span => span.DisplayName == "tool"))
        {
            var parent = Assert.Single(capture.Spans, span => span.SpanId == child.ParentSpanId);
            Assert.Equal(parent.GetTagItem("session.id"), child.GetTagItem("session.id"));
            Assert.Equal(parent.GetTagItem("user.id"), child.GetTagItem("user.id"));
            Assert.Equal(parent.TraceId, child.TraceId);
            Assert.Equal(ActivityStatusCode.Ok, child.Status);
        }
    }

    [Fact]
    public async Task Captured_content_and_exception_messages_redact_registered_credentials()
    {
        using var capture = new Capture();
        var telemetry = new ChatTelemetry(Options());
        var secret = Guid.NewGuid().ToString("N");
        telemetry.RegisterSecret(secret);
        await Assert.ThrowsAsync<InvalidOperationException>(() => telemetry.TraceAsync<int>("failure", "TOOL", span =>
        {
            span.Input("training data " + secret).Json("output.value", new { password = secret });
            throw new InvalidOperationException("Could not connect: " + secret);
        }));
        var result = Assert.Single(capture.Spans);
        Assert.Equal(ActivityStatusCode.Error, result.Status);
        var captured = string.Join(" ", result.TagObjects.Select(pair => pair.Value)) + result.StatusDescription
            + string.Join(" ", result.Events.SelectMany(item => item.Tags).Select(pair => pair.Value));
        Assert.DoesNotContain(secret, captured);
        Assert.Contains("training data", captured);
        Assert.Contains("[REDACTED]", captured);
    }

    [Fact]
    public async Task Metadata_mode_excludes_prompts_tool_arguments_and_exception_content()
    {
        using var capture = new Capture();
        var telemetry = new ChatTelemetry(Options() with { CaptureContent = false });
        var privateText = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => telemetry.TraceAsync<int>("failure", "LLM", span =>
        {
            span.Input(privateText).Output(privateText).Json("chat.data", new { value = privateText })
                .Messages("input", [new("user", privateText, [new("call", "tool", privateText)])]);
            throw new InvalidOperationException(privateText);
        }));
        var result = Assert.Single(capture.Spans);
        Assert.Null(result.GetTagItem("input.value"));
        Assert.Null(result.GetTagItem("llm.input_messages.0.message.content"));
        Assert.Null(result.GetTagItem("llm.input_messages.0.message.tool_calls.0.tool_call.function.arguments"));
        Assert.DoesNotContain(privateText, result.StatusDescription ?? "");
    }

    [Fact]
    public void Truncation_is_explicit_and_validation_rejection_is_not_overwritten_by_success()
    {
        using var capture = new Capture();
        var telemetry = new ChatTelemetry(Options() with { MaxContentCharacters = 1024 });
        using (var span = telemetry.Start("tool", "TOOL"))
        {
            span.Output(new string('x', 2048));
            span.Reject("duplicate entry");
            span.Complete();
        }
        var result = Assert.Single(capture.Spans);
        Assert.Equal(true, result.GetTagItem("chat.content_truncated"));
        Assert.Equal(2048, result.GetTagItem("output.value.original_characters"));
        Assert.EndsWith("[TRUNCATED]", Assert.IsType<string>(result.GetTagItem("output.value")));
        Assert.Equal(ActivityStatusCode.Error, result.Status);
        Assert.Equal("rejected", result.GetTagItem("chat.operation.outcome"));
    }

    [Fact]
    public async Task Models_capture_function_calls_usage_and_actual_http_attempts_without_double_counting()
    {
        using var capture = new Capture();
        var telemetry = new ChatTelemetry(Options());
        var terminal = new StubHandler("""
            {"model":"resolved-model","choices":[{"finish_reason":"tool_calls","message":{"content":null,
            "tool_calls":[{"id":"call-1","type":"function","function":{"name":"get_log","arguments":"{}"}}]}}],
            "usage":{"prompt_tokens":30,"completion_tokens":5,"total_tokens":35,
            "prompt_tokens_details":{"cached_tokens":12},"completion_tokens_details":{"reasoning_tokens":2}}}
            """);
        using var http = new HttpClient(new ChatProviderTelemetryHandler(telemetry) { InnerHandler = terminal });
        var client = new ChatGenerationClient(http, Settings(), true, "LiteLLM", telemetry);
        var result = await telemetry.TraceAsync("turn", "CHAIN", async span =>
        {
            span.Tag("session.id", "session");
            return await client.GenerateWithToolsAsync("model", "coach instructions", [new("user", "today")],
                [new("get_log", "read diary", """{"type":"object","properties":{}}""")], CancellationToken.None);
        });
        Assert.Equal("get_log", Assert.Single(result.ToolCalls).Name);
        var llm = Assert.Single(capture.Spans, span => Equals(span.GetTagItem("openinference.span.kind"), "LLM"));
        Assert.Equal(35, llm.GetTagItem("llm.token_count.total"));
        Assert.Equal(12L, llm.GetTagItem("llm.token_count.prompt_details.cache_read"));
        Assert.Equal("get_log", llm.GetTagItem("llm.output_messages.0.message.tool_calls.0.tool_call.function.name"));
        Assert.Equal("coach instructions", llm.GetTagItem("llm.input_messages.0.message.content"));
        var attempt = Assert.Single(capture.Spans, span => span.DisplayName == "provider.http");
        Assert.Equal(llm.SpanId, attempt.ParentSpanId);
        Assert.Null(attempt.GetTagItem("llm.token_count.total"));
        Assert.All(capture.Spans, span => Assert.Equal("session", span.GetTagItem("session.id")));
    }

    [Fact]
    public async Task Streaming_is_preserved_and_final_output_and_usage_are_captured()
    {
        using var capture = new Capture();
        var telemetry = new ChatTelemetry(Options());
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1,\"total_tokens\":11}}\n\n"
            + "data: [DONE]\n\n";
        using var http = new HttpClient(new ChatProviderTelemetryHandler(telemetry)
            { InnerHandler = new StubHandler(sse, "text/event-stream") });
        var client = new ChatGenerationClient(http, Settings(), true, "LiteLLM", telemetry);
        var deltas = new List<string>();
        var answer = await client.GenerateStreamingAsync("model", "instructions", "input",
            delta => { deltas.Add(delta); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("Hello", answer.Text);
        Assert.Equal("Hello", string.Concat(deltas));
        var llm = Assert.Single(capture.Spans, span => Equals(span.GetTagItem("openinference.span.kind"), "LLM"));
        Assert.Equal("Hello", llm.GetTagItem("output.value"));
        Assert.Equal(11, llm.GetTagItem("llm.token_count.total"));
    }

    [Fact]
    public async Task An_unavailable_collector_does_not_delay_or_fail_the_chat_operation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Reserve and close an ephemeral port; no service should accept this export.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var telemetry = services.AddChatTelemetry(Options() with { Endpoint = new Uri($"http://127.0.0.1:{port}/v1/traces") });
        await using var provider = services.BuildServiceProvider();
        var tracing = provider.GetRequiredService<TracerProvider>();
        var timer = Stopwatch.StartNew();
        var result = await telemetry.TraceAsync("offline-export", "CHAIN", _ => Task.FromResult(42));
        Assert.Equal(42, result);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
        tracing.ForceFlush(5000);
    }

    private static ChatTelemetryOptions Options() => new()
    {
        Enabled = true, CaptureContent = true, Endpoint = new Uri("http://127.0.0.1:6006/v1/traces"),
        ApiKey = Guid.NewGuid().ToString("N")
    };

    private static OpenAiSettings Settings() => new(Guid.NewGuid().ToString("N"), "https://model.invalid/v1",
        "model", "medium", "judge", "embedding", 2, 100, 100, 20);

    private sealed class Capture : IDisposable
    {
        public ConcurrentBag<Activity> Spans { get; } = [];
        private readonly ActivityListener _listener;
        public Capture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ChatTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Spans.Add(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }
        public void Dispose() => _listener.Dispose();
    }

    private sealed class StubHandler(string body, string contentType = "application/json") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) });
    }
}
