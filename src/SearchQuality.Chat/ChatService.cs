using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SearchQuality.Configuration;
using SearchQuality.Corpus;
using SearchQuality.Domain;
using SearchQuality.OpenAI;
using SearchQuality.Chat.Observability;

namespace SearchQuality.Chat;

/// <summary>
/// Простият чат поток: моделът разбира естествения език и избира инструмент,
/// а кодът изпълнява инструмента и пази транзакционните граници.
/// </summary>
public sealed class ChatService(
    LabSettings labSettings,
    ChatSettings chatSettings,
    GenerationClientResolver generation,
    ChatArchiveStore archive,
    ChatTelemetry? telemetry = null)
{
    private readonly ChatTelemetry _telemetry = telemetry ?? ChatTelemetry.Disabled;
    private sealed class UserSession
    {
        public readonly SemaphoreSlim TurnLock = new(1, 1);
        public readonly object MessagesSync = new();
        public readonly List<ChatMessage> Messages = [];
        public string? SummarySeed;
        public int LastContextTokens;
        public Guid? SessionId;
        public bool Loaded;
        public string? ActiveAgent;
    }

    private sealed class TurnState
    {
        public HashSet<string> MutatedArtifactKinds { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public DiaryConfirmationLog Diary { get; } = new();
    }

    private sealed record RetrievedContext(
        IReadOnlyList<ContextSnippet> Snippets,
        string Block);

    private static readonly ToolDefinition[] OrchestratorToolSet =
    [
        new(
            "find_specialists",
            "Връща наличните специалисти и областите, които поемат.",
            """{"type":"object","properties":{"need":{"type":"string","description":"кратко описание на нуждата"}},"required":[]}"""),
        new(
            "handoff_to",
            "Предава разговора на подходящ специалист.",
            """{"type":"object","properties":{"agent_key":{"type":"string","description":"ключ от find_specialists"},"task":{"type":"string","description":"точният поискан резултат и вече известните факти, без измислени изисквания"}},"required":["agent_key","task"]}"""),
        .. PersonalDataTools.Shared
    ];

    private static readonly ToolDefinition[] SpecialistToolSet =
    [
        new(
            "finish_task",
            "Задължителен преди финалния отговор на завършена задача: освобождава специалиста и връща следващия ход към основния асистент.",
            """{"type":"object","properties":{"summary":{"type":"string","description":"кратко резюме на действително свършеното"}},"required":["summary"]}"""),
        PersonalDataTools.SaveDeliverable,
        PersonalDataTools.ReplaceDeliverable,
        PersonalDataTools.EditDeliverable,
        .. PersonalDataTools.Shared
    ];

    private static readonly TimeSpan MicrophoneIdleTimeout = TimeSpan.FromHours(12);
    private static readonly TimeZoneInfo HomeTimeZone = ResolveHomeTimeZone();

    private readonly ConcurrentDictionary<string, UserSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _settingsLock = new();
    private int _contextTokenBudget = chatSettings.ContextTokenBudget;

    public static object DescribeToolCatalog() => new
    {
        chat = OrchestratorToolSet.Select(tool => new { tool.Name, tool.Description }),
        specialist = SpecialistToolSet.Select(tool => new { tool.Name, tool.Description })
    };

    private UserSession SessionFor(string userId) =>
        _sessions.GetOrAdd(userId, _ => new UserSession());

    private async Task EnsureLoadedAsync(
        string userId,
        UserSession session,
        CancellationToken cancellationToken)
    {
        if (session.Loaded)
        {
            return;
        }

        var restored = await archive.LoadActiveSessionAsync(userId, cancellationToken);
        if (restored is not null)
        {
            session.SessionId = restored.SessionId;
            session.SummarySeed = restored.SummarySeed;
            lock (session.MessagesSync)
            {
                session.Messages.AddRange(restored.Messages);
            }

            session.LastContextTokens = restored.LastContextTokens;
            session.ActiveAgent = restored.ActiveAgent;
        }

        session.Loaded = true;
    }

    public async Task<ChatRuntimeSettings> UpdateSettingsAsync(
        string? model,
        int? contextTokenBudget,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            await archive.UpdateAgentAsync(
                "chat",
                model.Trim(),
                enabled: null,
                activation: null,
                cancellationToken);
        }

        lock (_settingsLock)
        {
            if (contextTokenBudget is >= 1000 and <= 500000)
            {
                _contextTokenBudget = contextTokenBudget.Value;
            }
        }

        var runtime = await archive.GetAgentRuntimeAsync("chat", cancellationToken);
        return new ChatRuntimeSettings(runtime.Model, _contextTokenBudget);
    }

    public async Task<ChatStateResponse> GetStateAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveDescriptor> archives;
        try
        {
            archives = await archive.ListArchivesAsync(userId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            archives = [];
        }

        var agent = await archive.GetAgentRuntimeAsync("chat", cancellationToken);
        var agents = await archive.ListAgentsAsync(cancellationToken);
        var session = SessionFor(userId);
        var acquired = await session.TurnLock.WaitAsync(
            TimeSpan.FromSeconds(2),
            cancellationToken);
        if (!acquired && !session.Loaded)
        {
            await session.TurnLock.WaitAsync(cancellationToken);
            acquired = true;
        }

        try
        {
            if (acquired)
            {
                await EnsureLoadedAsync(userId, session, cancellationToken);
            }

            ChatMessage[] messages;
            lock (session.MessagesSync)
            {
                messages = session.Messages.ToArray();
            }

            return new ChatStateResponse(
                userId,
                messages,
                session.SummarySeed,
                CreateMeter(session),
                chatSettings,
                new ChatRuntimeSettings(agent.Model, _contextTokenBudget),
                archives,
                generation.SourceFor(agent.Provider, agent.Model))
            {
                ActiveAgent = session.ActiveAgent,
                ActiveAgentName = agents
                    .FirstOrDefault(item => item.Key == session.ActiveAgent)
                    ?.DisplayName
            };
        }
        finally
        {
            if (acquired)
            {
                session.TurnLock.Release();
            }
        }
    }

    public Task<ChatTurnResponse> SendAsync(
        string userId,
        string text,
        string strategy,
        int topK,
        CancellationToken cancellationToken,
        string channel = "web") =>
        SendAsync(
            userId,
            text,
            strategy,
            topK,
            onContext: null,
            onDelta: null,
            cancellationToken,
            channel);

    public Task<ChatTurnResponse> SendAsync(
        string userId,
        string text,
        string strategy,
        int topK,
        Func<IReadOnlyList<ContextSnippet>, Task>? onContext,
        Func<string, Task>? onDelta,
        CancellationToken cancellationToken,
        string channel = "web") =>
        _telemetry.TraceAsync("chat.turn", "CHAIN", async span =>
        {
            span.Input(text).Tag("user.id", userId).Tag("chat.channel", channel)
                .Tag("chat.turn.id", Guid.NewGuid().ToString("N"))
                .Tag("chat.retrieval.strategy", strategy).Tag("chat.retrieval.top_k", topK);
            var result = await SendCoreAsync(userId, text, strategy, topK, onContext, onDelta, span, cancellationToken);
            // Usage belongs only to individual LLM/embedding spans, preventing double counting by Phoenix.
            span.Output(result.Answer.Text).Tag("chat.final_agent", result.AgentKey)
                .Tag("chat.final_model", result.Model).Tag("chat.final_prompt_version", result.PromptVersion)
                .Tag("chat.context.tokens", result.Meter.UsedTokens)
                .Json("chat.steps", result.Steps).Json("chat.diary.confirmations", result.DiaryConfirmations);
            return result;
        });

    private async Task<ChatTurnResponse> SendCoreAsync(
        string userId,
        string text,
        string strategy,
        int topK,
        Func<IReadOnlyList<ContextSnippet>, Task>? onContext,
        Func<string, Task>? onDelta,
        ChatTelemetry.Scope turnSpan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var userText = text.Trim();
        var session = SessionFor(userId);
        await session.TurnLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(userId, session, cancellationToken);

            // Bind the session before retrieval, so every child span can be grouped in Phoenix.
            session.SessionId ??= await archive.CreateSessionAsync(userId, session.SummarySeed, cancellationToken);
            turnSpan.Tag("session.id", session.SessionId.Value.ToString());
            var contextTask = RetrieveContextAsync(
                userId,
                userText,
                strategy,
                topK,
                cancellationToken);
            var namesTask = AgentNamesAsync(cancellationToken);
            var artifactsTask = archive.ListCurrentArtifactsAsync(
                userId,
                cancellationToken);
            await Task.WhenAll(contextTask, namesTask, artifactsTask);

            var context = await contextTask;
            if (onContext is not null)
            {
                await onContext(context.Snippets);
            }

            var input = BuildInput(
                session,
                context.Block,
                userText,
                await namesTask,
                await artifactsTask);
            turnSpan.Json("chat.current_artifacts", await artifactsTask);

            var steps = new List<string>();
            var notes = new List<string>();
            var turnState = new TurnState();
            var (agent, generated) = await RunTurnAsync(
                userId,
                session,
                input,
                steps,
                notes,
                turnState,
                onDelta,
                cancellationToken);
            session.LastContextTokens = ContextTokensFor(generated);

            ChatMessage answer;
            lock (session.MessagesSync)
            {
                session.Messages.Add(new ChatMessage(
                    "user",
                    userText,
                    DateTimeOffset.UtcNow));
                foreach (var note in notes)
                {
                    session.Messages.Add(new ChatMessage(
                        "system",
                        note,
                        DateTimeOffset.UtcNow));
                }

                session.Messages.Add(new ChatMessage(
                    "assistant",
                    generated.Text,
                    DateTimeOffset.UtcNow,
                    agent.Key,
                    agent.Model,
                    agent.PromptVersion,
                    generated.Usage.InputTokens,
                    generated.Usage.OutputTokens,
                    steps.Count == 0 ? null : steps.ToArray()));
                answer = session.Messages[^1];
            }

            await _telemetry.TraceAsync("chat.persist_turn", "CHAIN", async persistence =>
            {
                await archive.AppendTurnAsync(
                session.SessionId.Value,
                userId,
                userText,
                notes,
                steps,
                generated.Text,
                agent,
                context.Snippets.Count == 0
                    ? null
                    : JsonSerializer.Serialize(context.Snippets.Select(snippet => new
                    {
                        chunkId = snippet.ChunkId,
                        documentId = snippet.DocumentId,
                        score = snippet.Score,
                        strategy = snippet.Strategy
                    })),
                generated.Usage,
                session.LastContextTokens,
                CancellationToken.None);
                persistence.Tag("chat.persistence.committed", true);
                return true;
            });

            ArchiveDescriptor? compactedTo = null;
            if (CreateMeter(session).CompactImminent)
            {
                try
                {
                    compactedTo = await CompactCoreAsync(
                        userId,
                        session,
                        keepSeed: true,
                        CancellationToken.None);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    turnSpan.Tag("chat.compaction.failed", true);
                }
            }

            return new ChatTurnResponse(
                answer,
                context.Snippets,
                context.Block,
                generated.Usage,
                CreateMeter(session),
                compactedTo,
                agent.Key,
                agent.Model,
                agent.PromptVersion)
            {
                Steps = steps,
                Notes = notes,
                DiaryConfirmations = turnState.Diary.Snapshot(),
                ActiveAgent = session.ActiveAgent,
                ActiveAgentName = session.ActiveAgent is null
                    ? null
                    : agent.DisplayName
            };
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    public async Task<ArchiveDescriptor?> CompactAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var session = SessionFor(userId);
        await session.TurnLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(userId, session, cancellationToken);
            return await CompactCoreAsync(
                userId,
                session,
                keepSeed: true,
                cancellationToken);
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    public async Task<ArchiveDescriptor?> StartNewChatAsync(
        string userId,
        bool archiveCurrent,
        CancellationToken cancellationToken)
    {
        var session = SessionFor(userId);
        await session.TurnLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(userId, session, cancellationToken);

            ArchiveDescriptor? archived = null;
            if (archiveCurrent && session.Messages.Count > 0)
            {
                archived = await CompactCoreAsync(
                    userId,
                    session,
                    keepSeed: false,
                    cancellationToken);
            }
            else if (session.SessionId is { } danglingSession)
            {
                await archive.CloseSessionAsync(
                    danglingSession,
                    null,
                    cancellationToken);
            }

            lock (session.MessagesSync)
            {
                session.Messages.Clear();
            }

            session.SummarySeed = null;
            session.LastContextTokens = 0;
            session.SessionId = null;
            session.ActiveAgent = null;
            return archived;
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    private async Task<(AgentRuntime Agent, GeneratedAnswer Answer)> RunTurnAsync(
        string userId,
        UserSession session,
        string input,
        List<string> steps,
        List<string> notes,
        TurnState turnState,
        Func<string, Task>? onDelta,
        CancellationToken cancellationToken)
    {
        var agents = await archive.ListAgentsAsync(cancellationToken);

        if (session.ActiveAgent is { } micKey)
        {
            ChatMessage? lastMessage;
            lock (session.MessagesSync)
            {
                lastMessage = session.Messages.LastOrDefault();
            }

            if (lastMessage is not null
                && DateTimeOffset.UtcNow - lastMessage.CreatedAt > MicrophoneIdleTimeout)
            {
                var staleName = agents.FirstOrDefault(agent => agent.Key == micKey)
                    ?.DisplayName ?? micKey;
                await SetMicrophoneAsync(session, null, cancellationToken);
                notes.Add($"— Разговорът с {staleName} беше неактивен и микрофонът се върна на Асистента —");
            }
        }

        if (session.ActiveAgent is { } activeKey)
        {
            var specialist = agents.FirstOrDefault(agent =>
                agent.Key == activeKey
                && agent.Enabled
                && agent.Kind == "specialist");
            if (specialist is not null)
            {
                var runtime = await archive.GetAgentRuntimeAsync(
                    activeKey,
                    cancellationToken);
                var answer = await RunSpecialistLoopAsync(
                    userId,
                    session,
                    runtime,
                    input,
                    steps,
                    notes,
                    turnState,
                    cancellationToken);
                if (onDelta is not null)
                {
                    await onDelta(answer.Text);
                }

                return (runtime, answer);
            }

            await SetMicrophoneAsync(session, null, cancellationToken);
        }

        var orchestrator = await archive.GetAgentRuntimeAsync(
            "chat",
            cancellationToken);
        var specialists = agents
            .Where(agent => agent.Kind == "specialist" && agent.Enabled)
            .ToArray();

        if (specialists.Length == 0)
        {
            return await _telemetry.TraceAsync("agent." + orchestrator.Key, "AGENT", async span =>
            {
                span.Agent(orchestrator).Input(input);
                var client = generation.Resolve(orchestrator.Provider);
                var answer = onDelta is null
                ? await client.GenerateAsync(
                    orchestrator.Model,
                    orchestrator.Instructions,
                    input,
                    cancellationToken)
                : await client.GenerateStreamingAsync(
                    orchestrator.Model,
                    orchestrator.Instructions,
                    input,
                    onDelta,
                    cancellationToken);
                span.Output(answer.Text);
                return (orchestrator, answer);
            });
        }

        var result = await RunOrchestratorLoopAsync(
            userId,
            session,
            orchestrator,
            specialists,
            input,
            steps,
            notes,
            turnState,
            cancellationToken);
        if (onDelta is not null)
        {
            await onDelta(result.Answer.Text);
        }

        return result;
    }

    private Task<(AgentRuntime Agent, GeneratedAnswer Answer)> RunOrchestratorLoopAsync(
        string userId, UserSession session, AgentRuntime orchestrator,
        IReadOnlyList<AgentInfo> specialists, string input, List<string> steps,
        List<string> notes, TurnState turnState, CancellationToken cancellationToken) =>
        _telemetry.TraceAsync("agent." + orchestrator.Key, "AGENT", async span =>
        {
            span.Agent(orchestrator).Input(input);
            var result = await RunOrchestratorLoopCoreAsync(userId, session, orchestrator, specialists,
                input, steps, notes, turnState, cancellationToken);
            span.Output(result.Answer.Text);
            return result;
        });

    private async Task<(AgentRuntime Agent, GeneratedAnswer Answer)> RunOrchestratorLoopCoreAsync(
        string userId,
        UserSession session,
        AgentRuntime orchestrator,
        IReadOnlyList<AgentInfo> specialists,
        string input,
        List<string> steps,
        List<string> notes,
        TurnState turnState,
        CancellationToken cancellationToken)
    {
        var client = generation.Resolve(orchestrator.Provider);
        var instructions = orchestrator.Instructions
            + ChatPromptPolicy.OrchestratorProtocol;
        var conversation = new List<ChatTurnMessage>
        {
            new("user", input)
        };
        var usage = TokenUsage.Empty;
        var lastInputTokens = 0;

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var result = await client.GenerateWithToolsAsync(
                orchestrator.Model,
                instructions,
                conversation,
                OrchestratorToolSet,
                cancellationToken);
            usage = AccumulateUsage(usage, result.Usage);
            lastInputTokens = Math.Max(lastInputTokens, result.Usage.InputTokens);

            if (result.ToolCalls.Count == 0)
            {
                return (
                    orchestrator,
                    FinalAnswer(result.Content, usage, lastInputTokens));
            }

            conversation.Add(new ChatTurnMessage(
                "assistant",
                null,
                result.ToolCalls));
            foreach (var call in result.ToolCalls)
            {
                if (call.Name == "find_specialists")
                {
                    using var toolSpan = _telemetry.Start("tool.find_specialists", "TOOL");
                    toolSpan.Tag("tool.name", call.Name).Tag("tool_call.id", call.Id).Input(call.ArgumentsJson);
                    steps.Add("🔎 потърси в регистъра на специалистите");
                    var registry = JsonSerializer.Serialize(
                        specialists.Select(specialist => new
                        {
                            agent_key = specialist.Key,
                            име = specialist.DisplayName,
                            поема = specialist.Activation
                        }));
                    toolSpan.Output(registry);
                    toolSpan.Complete();
                    conversation.Add(new ChatTurnMessage(
                        "tool",
                        registry,
                        ToolCallId: call.Id));
                    continue;
                }

                if (call.Name == "handoff_to")
                {
                    using var handoff = _telemetry.Start("tool.handoff_to", "TOOL");
                    handoff.Tag("tool.name", call.Name).Tag("tool_call.id", call.Id).Input(call.ArgumentsJson);
                    var (agentKey, task) = ParseHandoffArguments(
                        call.ArgumentsJson);
                    var target = specialists.FirstOrDefault(specialist =>
                        string.Equals(
                            specialist.Key,
                            agentKey,
                            StringComparison.OrdinalIgnoreCase));
                    if (target is null)
                    {
                        handoff.Reject("Unknown or disabled specialist");
                        handoff.Complete();
                        conversation.Add(new ChatTurnMessage(
                            "tool",
                            $"Непознат специалист '{agentKey}'. Налични: {string.Join(", ", specialists.Select(item => item.Key))}.",
                            ToolCallId: call.Id));
                        continue;
                    }

                    await SetMicrophoneAsync(
                        session,
                        target.Key,
                        cancellationToken);
                    notes.Add(
                        $"— Асистентът предаде разговора на {target.DisplayName}: {task} —");
                    steps.Add($"→ предаде на {target.DisplayName}");
                    handoff.Tag("chat.handoff.target", target.Key).Output("Active agent changed");
                    handoff.Complete();
                    handoff.Dispose();

                    var runtime = await archive.GetAgentRuntimeAsync(
                        target.Key,
                        cancellationToken);
                    var specialistInput =
                        $"{input}\n\nЗадача от Асистента: {task}";
                    var answer = await RunSpecialistLoopAsync(
                        userId,
                        session,
                        runtime,
                        specialistInput,
                        steps,
                        notes,
                        turnState,
                        cancellationToken);
                    return (
                        runtime,
                        answer with
                        {
                            Usage = AccumulateUsage(usage, answer.Usage),
                            ContextInputTokens = Math.Max(
                                lastInputTokens,
                                ContextTokensFor(answer))
                        });
                }

                var toolReply = await HandlePersonalDataToolAsync(
                    userId,
                    call,
                    steps,
                    allowDocumentWrites: false,
                    turnState,
                    cancellationToken);
                conversation.Add(new ChatTurnMessage(
                    "tool",
                    toolReply ?? $"Непознат инструмент '{call.Name}'.",
                    ToolCallId: call.Id));
            }
        }

        MarkAgentIterationLimit();
        return (
            orchestrator,
            new GeneratedAnswer(
                "Не стигнах до завършен отговор. Опитай отново с по-кратка формулировка.",
                usage,
                lastInputTokens));
    }

    private async Task<GeneratedAnswer> RunSpecialistLoopAsync(
        string userId,
        UserSession session,
        AgentRuntime specialist,
        string input,
        List<string> steps,
        List<string> notes,
        TurnState turnState,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunSpecialistLoopCoreAsync(
                userId,
                session,
                specialist,
                input,
                steps,
                notes,
                turnState,
                cancellationToken);
        }
        catch
        {
            try
            {
                await SetMicrophoneAsync(
                    session,
                    null,
                    CancellationToken.None);
            }
            catch
            {
                session.ActiveAgent = null;
            }

            throw;
        }
    }

    private Task<GeneratedAnswer> RunSpecialistLoopCoreAsync(
        string userId, UserSession session, AgentRuntime specialist, string input,
        List<string> steps, List<string> notes, TurnState turnState, CancellationToken cancellationToken) =>
        _telemetry.TraceAsync("agent." + specialist.Key, "AGENT", async span =>
        {
            span.Agent(specialist).Input(input);
            var result = await RunSpecialistIterationsAsync(userId, session, specialist, input,
                steps, notes, turnState, cancellationToken);
            span.Output(result.Text);
            return result;
        });

    private async Task<GeneratedAnswer> RunSpecialistIterationsAsync(
        string userId,
        UserSession session,
        AgentRuntime specialist,
        string input,
        List<string> steps,
        List<string> notes,
        TurnState turnState,
        CancellationToken cancellationToken)
    {
        var client = generation.Resolve(specialist.Provider);
        var instructions = specialist.Instructions
            + ChatPromptPolicy.SpecialistProtocol;
        var conversation = new List<ChatTurnMessage>
        {
            new("user", input)
        };
        var usage = TokenUsage.Empty;
        var lastInputTokens = 0;

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var result = await client.GenerateWithToolsAsync(
                specialist.Model,
                instructions,
                conversation,
                SpecialistToolSet,
                cancellationToken);
            usage = AccumulateUsage(usage, result.Usage);
            lastInputTokens = Math.Max(lastInputTokens, result.Usage.InputTokens);

            if (result.ToolCalls.Count == 0)
            {
                return FinalAnswer(result.Content, usage, lastInputTokens);
            }

            conversation.Add(new ChatTurnMessage(
                "assistant",
                null,
                result.ToolCalls));
            foreach (var call in result.ToolCalls)
            {
                if (call.Name == "finish_task")
                {
                    using var finish = _telemetry.Start("tool.finish_task", "TOOL");
                    finish.Tag("tool.name", call.Name).Tag("tool_call.id", call.Id).Input(call.ArgumentsJson);
                    var summary = ParseSummaryArgument(call.ArgumentsJson);
                    await SetMicrophoneAsync(
                        session,
                        null,
                        cancellationToken);
                    notes.Add(
                        $"— {specialist.DisplayName} приключи задачата: {summary} —");
                    steps.Add($"✓ {specialist.DisplayName} приключи");
                    finish.Output(summary);
                    finish.Complete();
                    conversation.Add(new ChatTurnMessage(
                        "tool",
                        "Задачата е приключена. Дай краткия финален резултат на потребителя.",
                        ToolCallId: call.Id));
                    continue;
                }

                var toolReply = await HandlePersonalDataToolAsync(
                    userId,
                    call,
                    steps,
                    allowDocumentWrites: true,
                    turnState,
                    cancellationToken);
                conversation.Add(new ChatTurnMessage(
                    "tool",
                    toolReply ?? $"Непознат инструмент '{call.Name}'.",
                    ToolCallId: call.Id));
            }
        }

        MarkAgentIterationLimit();
        return new GeneratedAnswer(
            "Не стигнах до завършен отговор. Опитай отново с по-кратка формулировка.",
            usage,
            lastInputTokens);
    }

    private static void MarkAgentIterationLimit()
    {
        if (System.Diagnostics.Activity.Current is { } activity && activity.Source.Name == ChatTelemetry.SourceName)
        {
            activity.SetTag("chat.agent.iteration_limit", true);
            activity.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Agent reached the tool iteration limit");
        }
    }

    private Task<string?> HandlePersonalDataToolAsync(
        string userId, ToolCall call, List<string> steps, bool allowDocumentWrites,
        TurnState turnState, CancellationToken cancellationToken) =>
        _telemetry.TraceAsync("tool." + call.Name, "TOOL", async span =>
        {
            span.Tag("tool.name", call.Name).Tag("tool_call.id", call.Id)
                .Content("tool.parameters", call.ArgumentsJson).Input(call.ArgumentsJson);
            var result = await HandlePersonalDataToolCoreAsync(userId, call, steps, allowDocumentWrites,
                turnState, span, cancellationToken);
            span.Output(result).Json("chat.diary.confirmations", turnState.Diary.Snapshot());
            if (result is null) span.Reject("Unknown tool");
            return result;
        });

    private async Task<string?> HandlePersonalDataToolCoreAsync(
        string userId,
        ToolCall call,
        List<string> steps,
        bool allowDocumentWrites,
        TurnState turnState,
        ChatTelemetry.Scope toolSpan,
        CancellationToken cancellationToken)
    {
        string Reject(string message)
        {
            toolSpan.Reject("Tool rejected the operation");
            return message;
        }

        switch (call.Name)
        {
            case "get_deliverable":
                {
                    if (PersonalDataTools.ParseKind(call.ArgumentsJson) is not { } kind)
                    {
                        return Reject("Липсва kind.");
                    }

                    var current = await archive.GetCurrentArtifactAsync(
                        userId,
                        kind,
                        cancellationToken);
                    steps.Add($"📄 прочете {kind}");
                    if (current is null)
                    {
                        return $"Няма записан документ от вид '{kind}'.";
                    }

                    var notes = await archive.GetArtifactNotesAsync(
                        userId,
                        kind,
                        50,
                        cancellationToken);
                    return JsonSerializer.Serialize(new
                    {
                        current_document_id = current.DocumentId,
                        вид = current.Kind,
                        заглавие = current.Title,
                        обновен = current.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
                        съдържание = current.Content,
                        бележки = notes.Select(note => new
                        {
                            дата = note.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                            текст = note.Note
                        })
                    });
                }

            case "save_deliverable":
                {
                    if (!allowDocumentWrites)
                    {
                        return Reject("Само специалист може да създава постоянни документи.");
                    }

                    var save = PersonalDataTools.ParseSave(call.ArgumentsJson);
                    if (save is null)
                    {
                        return Reject("Невалидни аргументи за save_deliverable.");
                    }

                    if (turnState.MutatedArtifactKinds.Contains(save.Kind))
                    {
                        return Reject($"Документът '{save.Kind}' вече е променен в този ход.");
                    }

                    if (await archive.GetCurrentArtifactAsync(
                            userId,
                            save.Kind,
                            cancellationToken) is not null)
                    {
                        return Reject($"Документът '{save.Kind}' вече съществува. Използвай edit_deliverable за локална промяна или replace_deliverable за пълна замяна.");
                    }

                    await SaveArtifactCoreAsync(
                        userId,
                        save,
                        cancellationToken);
                    turnState.MutatedArtifactKinds.Add(save.Kind);
                    steps.Add($"💾 създаде {save.Kind}");
                    return $"Документът '{save.Kind}' е създаден успешно.";
                }

            case "replace_deliverable":
                {
                    if (!allowDocumentWrites)
                    {
                        return Reject("Само специалист може да заменя постоянни документи.");
                    }

                    var replace = PersonalDataTools.ParseReplace(
                        call.ArgumentsJson);
                    if (replace is null)
                    {
                        return Reject("Невалидни аргументи за replace_deliverable.");
                    }

                    if (turnState.MutatedArtifactKinds.Contains(replace.Kind))
                    {
                        return Reject($"Документът '{replace.Kind}' вече е променен в този ход.");
                    }

                    var current = await archive.GetCurrentArtifactAsync(
                        userId,
                        replace.Kind,
                        cancellationToken);
                    if (current is null)
                    {
                        return Reject($"Няма текущ документ '{replace.Kind}'. Използвай save_deliverable.");
                    }

                    if (!string.Equals(
                            current.DocumentId,
                            replace.CurrentDocumentId,
                            StringComparison.Ordinal))
                    {
                        return Reject($"Текущата версия на '{replace.Kind}' е променена. Прочети я отново с get_deliverable и повтори замяната с новия current_document_id.");
                    }

                    await SaveReplacementArtifactCoreAsync(
                        userId,
                        replace,
                        cancellationToken);
                    turnState.MutatedArtifactKinds.Add(replace.Kind);
                    steps.Add($"💾 замени {replace.Kind}");
                    return $"Документът '{replace.Kind}' е заменен успешно.";
                }

            case "edit_deliverable":
                {
                    if (!allowDocumentWrites)
                    {
                        return Reject("Само специалист може да редактира постоянни документи.");
                    }

                    var edit = PersonalDataTools.ParseEdit(call.ArgumentsJson);
                    if (edit is null)
                    {
                        return Reject("Невалидни аргументи за edit_deliverable.");
                    }

                    if (turnState.MutatedArtifactKinds.Contains(edit.Kind))
                    {
                        return Reject($"Документът '{edit.Kind}' вече е променен в този ход.");
                    }

                    var current = await archive.GetCurrentArtifactAsync(
                        userId,
                        edit.Kind,
                        cancellationToken);
                    if (current is null)
                    {
                        return Reject($"Няма текущ документ '{edit.Kind}'.");
                    }

                    var (updated, error) = PersonalDataTools.ApplyExactEdits(
                        current.Content,
                        edit.Edits);
                    if (updated is null)
                    {
                        return Reject($"Документът не е променен: {error}.");
                    }

                    var stamp = DateTimeOffset.Now;
                    var title = edit.Title ?? current.Title;
                    var markdown = RefreshArtifactMetadata(
                        updated,
                        userId,
                        edit.Kind,
                        stamp);
                    await SaveArtifactMarkdownVersionAsync(
                        userId,
                        edit.Kind,
                        title,
                        markdown,
                        stamp,
                        cancellationToken);
                    turnState.MutatedArtifactKinds.Add(edit.Kind);
                    steps.Add($"✏️ редактира {edit.Kind}");
                    return $"Документът '{edit.Kind}' е редактиран успешно.";
                }

            case "add_note":
                {
                    var note = PersonalDataTools.ParseNote(call.ArgumentsJson);
                    if (note is null)
                    {
                        return Reject("Невалидни аргументи за add_note.");
                    }

                    await archive.AddArtifactNoteAsync(
                        userId,
                        note.Value.Kind,
                        note.Value.Text,
                        cancellationToken);
                    steps.Add($"📝 добави бележка към {note.Value.Kind}");
                    return "Бележката е записана.";
                }

            case "log_activity":
                {
                    var log = PersonalDataTools.ParseLog(call.ArgumentsJson);
                    if (log is null)
                    {
                        turnState.Diary.ReportFailure(
                            null,
                            null,
                            "Невалидни аргументи за log_activity.");
                        return Reject("Невалидни аргументи за log_activity.");
                    }

                    var prepared = await PrepareActivityLogsAsync(
                        userId,
                        [log],
                        cancellationToken);
                    if (prepared.Error is not null)
                    {
                        turnState.Diary.ReportFailure(
                            log.Kind,
                            log.Label,
                            prepared.Error);
                        return Reject(prepared.Error);
                    }

                    try
                    {
                        var id = await archive.LogActivityAsync(
                            userId,
                            log.Kind,
                            log.Label,
                            log.DataJson,
                            log.Note,
                            log.ReplacesId,
                            log.HappenedOn,
                            cancellationToken);
                        RecordActivitySuccess(turnState, log, id);
                        steps.Add($"📊 записа {log.Kind}: {log.Label}");
                        return $"Записано успешно с id {id}.";
                    }
                    catch (InactiveActivityReplacementException exception)
                    {
                        turnState.Diary.ReportFailure(
                            log.Kind,
                            log.Label,
                            exception.Message);
                        return Reject($"Не е записано: {exception.Message}");
                    }
                }

            case "log_activities":
                {
                    var logs = PersonalDataTools.ParseLogs(call.ArgumentsJson);
                    if (logs is null)
                    {
                        turnState.Diary.ReportFailure(
                            null,
                            null,
                            "Невалидни аргументи за log_activities.");
                        return Reject("Невалидни аргументи за log_activities.");
                    }

                    var prepared = await PrepareActivityLogsAsync(
                        userId,
                        logs,
                        cancellationToken);
                    if (prepared.Error is not null)
                    {
                        foreach (var log in logs)
                        {
                            turnState.Diary.ReportFailure(
                                log.Kind,
                                log.Label,
                                prepared.Error);
                        }

                        return Reject(prepared.Error);
                    }

                    try
                    {
                        var ids = await archive.LogActivitiesAsync(
                            userId,
                            logs,
                            cancellationToken);
                        for (var index = 0; index < logs.Count; index++)
                        {
                            RecordActivitySuccess(
                                turnState,
                                logs[index],
                                ids[index]);
                        }

                        steps.Add($"📊 записа {logs.Count} неща атомарно");
                        return $"Записани са {logs.Count} реда: {string.Join(", ", ids)}.";
                    }
                    catch (InactiveActivityReplacementException exception)
                    {
                        foreach (var log in logs)
                        {
                            turnState.Diary.ReportFailure(
                                log.Kind,
                                log.Label,
                                exception.Message);
                        }

                        return Reject($"Не е записано нищо: {exception.Message}");
                    }
                }

            case "void_activity":
                {
                    var request = PersonalDataTools.ParseVoid(call.ArgumentsJson);
                    if (request is null)
                    {
                        turnState.Diary.ReportFailure(
                            null,
                            null,
                            "Невалидни аргументи за void_activity.");
                        return Reject("Невалидни аргументи за void_activity.");
                    }

                    var result = await archive.VoidActivityAsync(
                        userId,
                        request.Id,
                        request.Reason,
                        cancellationToken);
                    if (result is null)
                    {
                        var message = $"Няма активен запис с id {request.Id}.";
                        turnState.Diary.ReportFailure(
                            "дневник",
                            $"id {request.Id}",
                            message);
                        return Reject(message);
                    }

                    turnState.Diary.ReportSuccess(new DiaryConfirmation(
                        DiaryConfirmationStatus.Voided,
                        "дневник",
                        result,
                        request.Id,
                        Message: request.Reason));
                    steps.Add($"🗑️ анулира дневников запис {request.Id}");
                    return $"Записът с id {request.Id} е анулиран.";
                }

            case "get_log":
                {
                    var query = PersonalDataTools.ParseLogQuery(
                        call.ArgumentsJson,
                        defaultDays: 14);
                    if (query.HappenedOnProvided && query.HappenedOn is null)
                    {
                        return Reject($"Невалиден happened_on '{query.HappenedOnRaw ?? "(празно)"}'. Използвай ГГГГ-ММ-ДД.");
                    }

                    var entries = await archive.GetActivityLogAsync(
                        userId,
                        query.Kind,
                        query.Days,
                        query.HappenedOn,
                        limit: 100,
                        cancellationToken);
                    steps.Add("📋 прочете дневника");
                    return entries.Count == 0
                        ? "Няма активни дневникови записи за посочения обхват."
                        : JsonSerializer.Serialize(new
                        {
                            източник = "SQL дневник",
                            записи = entries.Select(entry => new
                            {
                                id = entry.Id,
                                дата = entry.HappenedAt.ToString(
                                    "O",
                                    CultureInfo.InvariantCulture),
                                вид = entry.Kind,
                                какво = entry.Label,
                                data = entry.DataJson,
                                бележка = entry.Note
                            })
                        });
                }

            case "get_progress":
                {
                    if (PersonalDataTools.ParseKind(call.ArgumentsJson) is not { } kind)
                    {
                        return Reject("Липсва kind.");
                    }

                    var query = PersonalDataTools.ParseLogQuery(
                        call.ArgumentsJson,
                        defaultDays: 60);
                    if (query.HappenedOnProvided && query.HappenedOn is null)
                    {
                        return Reject($"Невалиден happened_on '{query.HappenedOnRaw ?? "(празно)"}'. Използвай ГГГГ-ММ-ДД.");
                    }

                    IReadOnlyList<string>? labels = null;
                    if (query.HappenedOn is { } requestedDay)
                    {
                        var dayEntries = await archive.GetActivityLogAsync(
                            userId,
                            kind,
                            query.Days,
                            requestedDay,
                            limit: 100,
                            cancellationToken);
                        labels = dayEntries
                            .Select(entry => entry.Label)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        if (labels.Count == 0)
                        {
                            return $"Няма активни записи от вид '{kind}' за {requestedDay:dd.MM.yyyy}.";
                        }
                    }

                    var rows = await archive.GetProgressAsync(
                        userId,
                        kind,
                        query.Days,
                        labels,
                        cancellationToken);
                    steps.Add($"📈 прочете прогреса за {kind}");
                    return rows.Count == 0
                        ? "Няма числови записи за посочения период."
                        : JsonSerializer.Serialize(new
                        {
                            източник = "SQL агрегация",
                            редове = rows.Select(row => new
                            {
                                седмица = row.Week,
                                какво = row.Label,
                                поле = row.Field,
                                мин = row.Min,
                                средно = row.Avg,
                                макс = row.Max,
                                брой = row.Count
                            })
                        });
                }

            default:
                return null;
        }
    }

    private sealed record PreparedActivityLogs(string? Error);

    private async Task<PreparedActivityLogs> PrepareActivityLogsAsync(
        string userId,
        IReadOnlyList<PersonalDataTools.LogArgs> logs,
        CancellationToken cancellationToken)
    {
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in logs)
        {
            if (log.HappenedOnProvided && log.HappenedOn is null)
            {
                return new PreparedActivityLogs(
                    $"Не е записано нищо: happened_on '{log.HappenedOnRaw ?? "(празно)"}' не е валидна дата ГГГГ-ММ-ДД.");
            }

            if (log.ReplacesId is not null || log.AllowDuplicate)
            {
                continue;
            }

            var dayKey = log.HappenedOn?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture) ?? "<today>";
            var pendingKey = string.Join(
                '\u001f',
                log.Kind,
                log.Label,
                dayKey);
            if (!pending.Add(pendingKey))
            {
                return new PreparedActivityLogs(
                    $"Не е записано нищо: batch-ът съдържа повторен ред за '{log.Label}' в същия ден.");
            }

            if (await archive.GetSameDayEntryAsync(
                    userId,
                    log.Kind,
                    log.Label,
                    log.HappenedOn,
                    cancellationToken) is { } existing)
            {
                return new PreparedActivityLogs(
                    $"Не е записано нищо: вече има активен запис за '{log.Label}' с id {existing.Id}. За корекция използвай replaces_id={existing.Id}; за действително второ изпълнение използвай allow_duplicate=true.");
            }
        }

        return new PreparedActivityLogs(null);
    }

    private static void RecordActivitySuccess(
        TurnState turnState,
        PersonalDataTools.LogArgs log,
        long id)
    {
        turnState.Diary.ReportSuccess(new DiaryConfirmation(
            log.ReplacesId is null
                ? DiaryConfirmationStatus.Recorded
                : DiaryConfirmationStatus.Corrected,
            log.Kind,
            log.Label,
            id,
            DataJson: log.DataJson,
            Note: log.Note,
            Message: log.ReplacesId is { } oldId
                ? $"Корекция на запис id {oldId}."
                : null));
    }

    private async Task SaveArtifactCoreAsync(
        string userId,
        PersonalDataTools.SaveArgs save,
        CancellationToken cancellationToken)
    {
        var stamp = DateTimeOffset.Now;
        var markdown = BuildArtifactMarkdown(
            userId,
            save.Kind,
            save.Title,
            save.Content,
            stamp);
        await SaveArtifactMarkdownVersionAsync(
            userId,
            save.Kind,
            save.Title,
            markdown,
            stamp,
            cancellationToken);
    }

    private async Task SaveReplacementArtifactCoreAsync(
        string userId,
        PersonalDataTools.ReplaceArgs replace,
        CancellationToken cancellationToken)
    {
        var stamp = DateTimeOffset.Now;
        var markdown = BuildReplacementArtifactMarkdown(
            userId,
            replace.Kind,
            replace.Title,
            replace.Content,
            stamp);
        await SaveArtifactMarkdownVersionAsync(
            userId,
            replace.Kind,
            replace.Title,
            markdown,
            stamp,
            cancellationToken);
    }

    public static string BuildArtifactMarkdown(
        string userId,
        string kind,
        string title,
        string content,
        DateTimeOffset stamp)
    {
        var body = content.Trim();
        var metadata =
            $"Потребител: {userId} · Вид: {kind} · Записано: {stamp:dd.MM.yyyy HH:mm}";
        if (!body.StartsWith("# ", StringComparison.Ordinal))
        {
            return $"# {title}\n\n{metadata}\n\n{body}";
        }

        var lineBreak = body.IndexOfAny(['\r', '\n']);
        if (lineBreak < 0)
        {
            return $"{body}\n\n{metadata}";
        }

        var bodyStart = lineBreak;
        while (bodyStart < body.Length && body[bodyStart] is '\r' or '\n')
        {
            bodyStart++;
        }

        return bodyStart == body.Length
            ? $"{body[..lineBreak]}\n\n{metadata}"
            : $"{body[..lineBreak]}\n\n{metadata}\n\n{body[bodyStart..]}";
    }

    public static string BuildReplacementArtifactMarkdown(
        string userId,
        string kind,
        string title,
        string content,
        DateTimeOffset stamp)
    {
        var body = content.Trim();
        var refreshed = RefreshArtifactMetadata(
            body,
            userId,
            kind,
            stamp);
        return string.Equals(refreshed, body, StringComparison.Ordinal)
            ? BuildArtifactMarkdown(userId, kind, title, body, stamp)
            : refreshed;
    }

    private async Task SaveArtifactMarkdownVersionAsync(
        string userId,
        string kind,
        string title,
        string markdown,
        DateTimeOffset stamp,
        CancellationToken cancellationToken)
    {
        var id = $"artifact-{userId}-{kind}-{stamp:yyyyMMdd-HHmmssfff}";
        var fileName = $"{id}.md";
        var directory = Path.Combine(
            labSettings.RootDirectory,
            "data",
            "artifacts");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, fileName),
            markdown,
            cancellationToken);

        await archive.ArchiveAsync(
            userId,
            new SourceDocument(id, title, fileName, markdown),
            cancellationToken,
            documentType: "artifact");
        await archive.SetCurrentArtifactAsync(
            userId,
            kind,
            id,
            cancellationToken);
    }

    private static string RefreshArtifactMetadata(
        string markdown,
        string userId,
        string kind,
        DateTimeOffset stamp)
    {
        var lineStart = 0;
        for (var lineNumber = 0;
             lineNumber < 8 && lineStart < markdown.Length;
             lineNumber++)
        {
            var lineBreak = markdown.IndexOf('\n', lineStart);
            var lineEnd = lineBreak < 0 ? markdown.Length : lineBreak;
            if (lineEnd > lineStart && markdown[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            var line = markdown.AsSpan(lineStart, lineEnd - lineStart);
            if (line.StartsWith("Потребител:", StringComparison.Ordinal)
                && line.Contains(" · Вид:", StringComparison.Ordinal)
                && line.Contains(" · Записано:", StringComparison.Ordinal))
            {
                var replacement =
                    $"Потребител: {userId} · Вид: {kind} · Записано: {stamp:dd.MM.yyyy HH:mm}";
                return string.Concat(
                    markdown.AsSpan(0, lineStart),
                    replacement,
                    markdown.AsSpan(lineEnd));
            }

            if (lineBreak < 0)
            {
                break;
            }

            lineStart = lineBreak + 1;
        }

        return markdown;
    }

    public async Task<string?> HandbackAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var session = SessionFor(userId);
        await session.TurnLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(userId, session, cancellationToken);
            if (session.ActiveAgent is null)
            {
                return null;
            }

            var previous = session.ActiveAgent;
            await SetMicrophoneAsync(session, null, cancellationToken);
            var note = "— Потребителят върна разговора на Асистента —";
            lock (session.MessagesSync)
            {
                session.Messages.Add(new ChatMessage(
                    "system",
                    note,
                    DateTimeOffset.UtcNow));
            }

            if (session.SessionId is { } sessionId)
            {
                await archive.AppendNoteAsync(
                    sessionId,
                    userId,
                    note,
                    cancellationToken);
            }

            return previous;
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    private async Task SetMicrophoneAsync(
        UserSession session,
        string? agentKey,
        CancellationToken cancellationToken)
    {
        session.ActiveAgent = agentKey;
        if (session.SessionId is { } sessionId)
        {
            await archive.SetActiveAgentAsync(
                sessionId,
                agentKey,
                cancellationToken);
        }
    }

    private static GeneratedAnswer FinalAnswer(
        string? content,
        TokenUsage usage,
        int contextInputTokens) =>
        new(
            string.IsNullOrWhiteSpace(content)
                ? "Не успях да съставя отговор. Опитай да преформулираш."
                : content.Trim(),
            usage,
            contextInputTokens);

    private static TokenUsage AccumulateUsage(
        TokenUsage total,
        TokenUsage step) =>
        new(
            Math.Max(total.InputTokens, step.InputTokens),
            total.OutputTokens + step.OutputTokens,
            total.TotalTokens + step.TotalTokens);

    private static (string AgentKey, string Task) ParseHandoffArguments(
        string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            return (
                root.TryGetProperty("agent_key", out var key)
                    ? key.GetString() ?? ""
                    : "",
                root.TryGetProperty("task", out var task)
                    ? task.GetString() ?? ""
                    : "");
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

    private static string ParseSummaryArgument(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty(
                    "summary",
                    out var summary)
                ? summary.GetString() ?? "задачата е приключена"
                : "задачата е приключена";
        }
        catch (JsonException)
        {
            return "задачата е приключена";
        }
    }

    private ContextMeter CreateMeter(UserSession session) =>
        CreateMeter(session.LastContextTokens, _contextTokenBudget);

    public static int ContextTokensFor(GeneratedAnswer answer) =>
        answer.ContextInputTokens is > 0
            ? answer.ContextInputTokens.Value
            : answer.Usage.InputTokens;

    public static ContextMeter CreateMeter(int usedTokens, int budgetTokens)
    {
        var percent = budgetTokens <= 0
            ? 0
            : Math.Min(1d, usedTokens / (double)budgetTokens);
        return new ContextMeter(
            usedTokens,
            budgetTokens,
            percent,
            percent >= ChatSettings.WarningThreshold,
            percent >= ChatSettings.CompactThreshold);
    }

    private async Task<RetrievedContext> RetrieveContextAsync(
        string userId,
        string query,
        string strategy,
        int topK,
        CancellationToken cancellationToken)
    {
        using var span = _telemetry.Start("memory.retrieve", "RETRIEVER");
        span.Input(query).Tag("chat.retrieval.strategy", strategy).Tag("chat.retrieval.top_k", topK);
        try
        {
            if (!await archive.HasArchivesAsync(userId, cancellationToken))
            {
                span.Tag("chat.retrieval.hit_count", 0).Tag("chat.retrieval.has_archives", false);
                span.Complete();
                return new RetrievedContext([], string.Empty);
            }

            var result = await archive.SearchAsync(
                userId,
                query,
                strategy,
                topK,
                cancellationToken);
            var snippets = result.Hits
                .Select(hit => new ContextSnippet(
                    hit.DocumentId,
                    hit.DocumentTitle,
                    hit.ChunkId,
                    hit.Text,
                    hit.Score,
                    strategy))
                .ToArray();
            span.Tag("chat.retrieval.hit_count", snippets.Length);
            for (var i = 0; i < snippets.Length; i++)
            {
                var snippet = snippets[i];
                var prefix = $"retrieval.documents.{i}.document";
                span.Tag(prefix + ".id", snippet.ChunkId).Tag(prefix + ".score", snippet.Score)
                    .Content(prefix + ".content", snippet.Text)
                    .Json(prefix + ".metadata", new { snippet.DocumentId, snippet.DocumentTitle, snippet.Strategy });
            }
            if (snippets.Length == 0)
            {
                span.Complete();
                return new RetrievedContext([], string.Empty);
            }

            var block = new StringBuilder();
            block.AppendLine(
                "Контекст от предишни разговори. Може да е остарял или нерелевантен; текущите документи и SQL инструментите имат предимство:");
            foreach (var snippet in snippets)
            {
                block
                    .Append("--- ")
                    .Append(snippet.DocumentTitle)
                    .AppendLine(" ---")
                    .AppendLine(snippet.Text);
            }

            span.Output(block.ToString());
            span.Complete();
            return new RetrievedContext(snippets, block.ToString());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            span.Fail(exception);
            span.Tag("chat.retrieval.fallback", "empty_context");
            return new RetrievedContext([], string.Empty);
        }
    }

    private async Task<Dictionary<string, string>> AgentNamesAsync(
        CancellationToken cancellationToken)
    {
        var agents = await archive.ListAgentsAsync(cancellationToken);
        return agents.ToDictionary(
            agent => agent.Key,
            agent => agent.DisplayName,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string SpeakerLabel(
        ChatMessage message,
        IReadOnlyDictionary<string, string> agentNames) =>
        message.Role switch
        {
            "user" => "Потребител",
            _ => message.AgentKey is not null
                 && agentNames.TryGetValue(message.AgentKey, out var name)
                ? name
                : "Асистент"
        };

    private static string BuildInput(
        UserSession session,
        string contextBlock,
        string userText,
        IReadOnlyDictionary<string, string> agentNames,
        IReadOnlyList<ArtifactSummary> artifacts)
    {
        var builder = new StringBuilder();
        var localNow = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            HomeTimeZone);
        builder
            .Append("Текущи дата и час (Europe/Sofia): ")
            .AppendLine(localNow.ToString(
                "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture))
            .AppendLine();

        if (artifacts.Count > 0)
        {
            builder.AppendLine(
                "Записани текущи документи (прочети пълния текст с get_deliverable):");
            foreach (var artifact in artifacts)
            {
                builder
                    .Append("- ")
                    .Append(artifact.Kind)
                    .Append(": ")
                    .Append(artifact.Title)
                    .Append(" (обновен ")
                    .Append(artifact.UpdatedAt.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture))
                    .AppendLine(")");
            }

            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(contextBlock))
        {
            builder.AppendLine(contextBlock);
        }

        if (!string.IsNullOrWhiteSpace(session.SummarySeed))
        {
            builder.AppendLine("Резюме на по-ранната част от текущия разговор:");
            builder.AppendLine(session.SummarySeed);
            builder.AppendLine();
        }

        if (session.Messages.Count > 0)
        {
            builder.AppendLine("Разговор досега:");
            foreach (var message in session.Messages)
            {
                if (message.Role == "system")
                {
                    builder.AppendLine(message.Text);
                    continue;
                }

                builder
                    .Append(SpeakerLabel(message, agentNames))
                    .Append(": ")
                    .AppendLine(message.Text);
            }

            builder.AppendLine();
        }

        builder.Append("Потребител: ").AppendLine(userText);
        return builder.ToString();
    }

    public static string BuildCompactionTranscript(
        IReadOnlyList<ChatMessage> messages)
    {
        var transcript = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.Role == "system")
            {
                continue;
            }

            transcript
                .Append('[')
                .Append(message.CreatedAt.ToString(
                    "O",
                    CultureInfo.InvariantCulture))
                .Append("] ")
                .Append(message.Role == "user" ? "Потребител" : "Асистент")
                .Append(": ")
                .AppendLine(message.Text);
        }

        return transcript.ToString();
    }

    public static string NormalizeCompactionSummary(string text)
    {
        var normalized = text.Trim();
        return normalized.Length == 0
            ? "Няма нови дългосрочни факти за запазване."
            : normalized;
    }

    private Task<ArchiveDescriptor?> CompactCoreAsync(
        string userId, UserSession session, bool keepSeed, CancellationToken cancellationToken) =>
        _telemetry.TraceAsync("memory.compact", "CHAIN", async span =>
        {
            span.Tag("user.id", userId).Tag("session.id", session.SessionId?.ToString())
                .Tag("chat.compaction.keep_seed", keepSeed);
            var result = await CompactSessionAsync(userId, session, keepSeed, span, cancellationToken);
            span.Tag("chat.compaction.next_session", session.SessionId?.ToString()).Json("chat.compaction.archive", result);
            return result;
        });

    private async Task<ArchiveDescriptor?> CompactSessionAsync(
        string userId,
        UserSession session,
        bool keepSeed,
        ChatTelemetry.Scope span,
        CancellationToken cancellationToken)
    {
        if (session.Messages.Count == 0)
        {
            return null;
        }

        var transcript = BuildCompactionTranscript(session.Messages);
        var compactor = await archive.GetAgentRuntimeAsync(
            "compactor",
            cancellationToken);
        span.Agent(compactor).Input(transcript);
        var summary = await generation.Resolve(compactor.Provider).GenerateAsync(
            compactor.Model,
            compactor.Instructions + ChatPromptPolicy.CompactorProtocol,
            transcript,
            cancellationToken);
        var normalizedSummary = NormalizeCompactionSummary(summary.Text);
        span.Output(normalizedSummary);

        var timestamp = DateTimeOffset.Now;
        var id = $"chat-{userId}-{timestamp:yyyyMMdd-HHmmss}";
        var fileName = $"{id}.md";
        var title = $"Чат резюме {timestamp:dd.MM.yyyy HH:mm}";
        var markdown = $"""
            # {title}

            Потребител: {userId}
            Архивиран: {timestamp.ToString("O", CultureInfo.InvariantCulture)}
            Резюмирано от: {compactor.Model} · промпт compactor v{compactor.PromptVersion}

            {normalizedSummary}
            """;

        var directory = Path.Combine(
            labSettings.RootDirectory,
            "data",
            "chat-history");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, fileName),
            markdown,
            cancellationToken);

        await archive.ArchiveAsync(
            userId,
            new SourceDocument(id, title, fileName, markdown),
            cancellationToken);

        var previousSession = session.SessionId;
        session.SessionId = keepSeed
            ? await archive.CreateSessionAsync(
                userId,
                normalizedSummary,
                cancellationToken)
            : null;

        if (previousSession is { } closedSession)
        {
            await archive.CloseSessionAsync(
                closedSession,
                id,
                cancellationToken);
        }

        lock (session.MessagesSync)
        {
            session.Messages.Clear();
        }

        session.SummarySeed = keepSeed ? normalizedSummary : null;
        session.LastContextTokens = 0;
        session.ActiveAgent = null;

        return new ArchiveDescriptor(
            id,
            title,
            fileName,
            timestamp,
            markdown.Length);
    }

    private static TimeZoneInfo ResolveHomeTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Sofia");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
        }
    }
}
