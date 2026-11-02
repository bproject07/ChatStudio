using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SearchQuality.Domain;
using SearchQuality.OpenAI;

namespace SearchQuality.Chat.Observability;

/// <summary>OpenInference spans, with explicit content capture and credential redaction.</summary>
public sealed class ChatTelemetry(ChatTelemetryOptions options)
{
    private readonly ChatTelemetryOptions _options = options;
    public const string SourceName = "SearchQuality.Chat";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
    public static readonly ChatTelemetry Disabled = new(new());
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private static readonly Regex CredentialPattern = new(
        "(?i)(Bearer\\s+)[A-Za-z0-9._~+/=-]+|\\bsk-[A-Za-z0-9_-]{12,}|((?:password|pwd|api[_-]?key|access[_-]?token|secret)\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\s\\\"';,}]+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public void RegisterSecret(string? value)
    {
        if (!string.IsNullOrEmpty(value)) lock (_sync) _secrets.Add(value);
    }

    internal string Sanitize(string value)
    {
        lock (_sync)
            foreach (var secret in _secrets.OrderByDescending(item => item.Length))
                value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        try { return CredentialPattern.Replace(value, match => match.Groups[1].Value + match.Groups[2].Value + "[REDACTED]"); }
        catch (RegexMatchTimeoutException) { return "[REDACTED: content could not be safely inspected]"; }
    }

    public Scope Start(string name, string kind = "CHAIN")
    {
        var parent = Activity.Current;
        // ASP.NET creates an ambient request activity even without HTTP instrumentation.
        // Export a real root, with a link to that request, instead of an orphaned child.
        var detached = _options.Enabled && Source.HasListeners() && parent is not null
            && parent.Source.Name != SourceName;
        if (detached) Activity.Current = null;
        var activity = _options.Enabled ? Source.StartActivity(name, ActivityKind.Internal,
            parentContext: detached ? default : parent?.Context ?? default,
            links: detached ? [new ActivityLink(parent!.Context)] : null) : null;
        if (detached && activity is null) Activity.Current = parent;
        if (activity is not null)
        {
            activity.SetTag("openinference.span.kind", kind);
            activity.SetTag("chat.content_capture", _options.CaptureContent);
            foreach (var key in new[] { "session.id", "user.id", "chat.turn.id", "chat.channel", "chat.agent.key", "chat.prompt.version" })
                if (parent?.GetTagItem(key) is { } value) activity.SetTag(key, value);
        }
        return new Scope(this, activity, detached ? parent : null);
    }

    public async Task<T> TraceAsync<T>(string name, string kind, Func<Scope, Task<T>> action)
    {
        using var span = Start(name, kind);
        try
        {
            var result = await action(span);
            span.Complete();
            return result;
        }
        catch (Exception exception)
        {
            span.Fail(exception);
            throw;
        }
    }

    public sealed class Scope(ChatTelemetry telemetry, Activity? activity, Activity? detachedParent = null) : IDisposable
    {
        private bool _completed;
        private bool _disposed;
        public Activity? Activity => activity;
        public Scope Tag(string key, object? value)
        {
            if (value is string text) value = telemetry.Sanitize(text);
            activity?.SetTag(key, value);
            return this;
        }

        public Scope Content(string key, string? value)
        {
            if (activity is null || !telemetry._options.CaptureContent || value is null) return this;
            value = telemetry.Sanitize(value);
            if (value.Length > telemetry._options.MaxContentCharacters)
            {
                Tag("chat.content_truncated", true);
                Tag(key + ".original_characters", value.Length);
                value = value[..telemetry._options.MaxContentCharacters] + "\n[TRUNCATED]";
            }
            activity.SetTag(key, value);
            return this;
        }

        public Scope Input(string? text) => Content("input.value", text).Tag("input.mime_type", "text/plain");
        public Scope Output(string? text) => Content("output.value", text).Tag("output.mime_type", "text/plain");
        public Scope Json(string key, object? value)
        {
            if (activity is null || !telemetry._options.CaptureContent) return this;
            return Content(key, JsonSerializer.Serialize(value));
        }

        public Scope Usage(TokenUsage usage) => Tag("llm.token_count.prompt", usage.InputTokens)
            .Tag("llm.token_count.completion", usage.OutputTokens).Tag("llm.token_count.total", usage.TotalTokens);

        public Scope Agent(AgentRuntime agent) => Tag("chat.agent.key", agent.Key)
            .Tag("chat.prompt.version", agent.PromptVersion).Tag("chat.model", agent.Model)
            .Tag("chat.provider", agent.Provider).Tag("llm.prompt_template.version", agent.PromptVersion.ToString());

        public Scope Messages(string direction, IReadOnlyList<ChatTurnMessage> messages)
        {
            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                var prefix = $"llm.{direction}_messages.{index}.message";
                Tag(prefix + ".role", message.Role);
                Content(prefix + ".content", message.Content);
                Tag(prefix + ".tool_call_id", message.ToolCallId);
                if (message.ToolCalls is not { } calls) continue;
                for (var callIndex = 0; callIndex < calls.Count; callIndex++)
                {
                    var call = calls[callIndex];
                    var callPrefix = prefix + $".tool_calls.{callIndex}.tool_call";
                    Tag(callPrefix + ".id", call.Id).Tag(callPrefix + ".function.name", call.Name)
                        .Content(callPrefix + ".function.arguments", call.ArgumentsJson);
                }
            }
            return this;
        }

        public void Reject(string reason)
        {
            Tag("chat.operation.outcome", "rejected");
            activity?.SetStatus(ActivityStatusCode.Error, telemetry.Sanitize(reason));
        }

        public void Fail(Exception exception)
        {
            _completed = true;
            var type = exception.GetType().FullName ?? exception.GetType().Name;
            Tag("error.type", type);
            Tag("chat.cancelled", exception is OperationCanceledException);
            var description = telemetry._options.CaptureContent ? telemetry.Sanitize(exception.Message) : type;
            activity?.SetStatus(ActivityStatusCode.Error, description);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                ["exception.type"] = type,
                ["exception.message"] = description
            }));
        }

        public void Complete()
        {
            _completed = true;
            if (activity?.Status == ActivityStatusCode.Unset) activity.SetStatus(ActivityStatusCode.Ok);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_completed && activity?.Status == ActivityStatusCode.Unset)
                activity.SetStatus(ActivityStatusCode.Error, "Operation did not complete");
            activity?.Dispose();
            if (detachedParent is not null) System.Diagnostics.Activity.Current = detachedParent;
        }
    }
}
