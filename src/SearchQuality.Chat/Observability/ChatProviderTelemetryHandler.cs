using System.Diagnostics;
using System.Text.Json;

namespace SearchQuality.Chat.Observability;

/// <summary>Each actual HTTP attempt is visible, including retries. Never records headers or URLs containing credentials.</summary>
public sealed class ChatProviderTelemetryHandler(ChatTelemetry telemetry) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var parent = Activity.Current;
        // Do not trace background model-list requests, nor the telemetry exporter's own HTTP client.
        if (parent?.Source.Name != ChatTelemetry.SourceName) return base.SendAsync(request, cancellationToken);
        return telemetry.TraceAsync("provider.http", "CHAIN", async span =>
        {
            span.Tag("http.request.method", request.Method.Method)
                .Tag("server.address", request.RequestUri?.Host)
                .Tag("url.path", request.RequestUri?.AbsolutePath);
            if (request.Content is not null)
                span.Content("chat.provider.request", await request.Content.ReadAsStringAsync(cancellationToken));
            var response = await base.SendAsync(request, cancellationToken);
            try
            {
                span.Tag("http.response.status_code", (int)response.StatusCode);
                if (!response.IsSuccessStatusCode) span.Reject("HTTP " + (int)response.StatusCode);
                var isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
                if (isJson)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    // Embedding vectors add megabytes and no explanation; their input and dimensions are on the embedding span.
                    if (request.RequestUri?.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal) != true)
                        span.Content("chat.provider.response", body);
                    AnnotateUsage(parent, body);
                }
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        });
    }

    private static void AnnotateUsage(Activity parent, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                parent.SetTag("chat.response.model", model.GetString());
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
            foreach (var field in new[] { "prompt_tokens_details", "input_tokens_details" })
                if (usage.TryGetProperty(field, out var details) && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("cached_tokens", out var cached) && cached.TryGetInt64(out var count))
                    parent.SetTag("llm.token_count.prompt_details.cache_read", count);
            foreach (var field in new[] { "completion_tokens_details", "output_tokens_details" })
                if (usage.TryGetProperty(field, out var details) && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("reasoning_tokens", out var reasoning) && reasoning.TryGetInt64(out var count))
                    parent.SetTag("llm.token_count.completion_details.reasoning", count);
        }
        catch (JsonException) { /* Telemetry must not change provider parsing semantics. */ }
        catch (InvalidOperationException) { /* Unexpected usage types are not an application failure. */ }
    }
}
