using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using SearchQuality.Chat;
using SearchQuality.Chat.Observability;
using SearchQuality.Configuration;
using SearchQuality.OpenAI;

var root = ProjectRootLocator.Find(Environment.CurrentDirectory);
var labSettings = LabSettingsLoader.Load(root);
var chatSettings = ChatSettingsLoader.Load(root);
using var networkAccess = ChatNetworkAccess.FromEnvironment();
var streamJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var builder = WebApplication.CreateBuilder(args);
var telemetry = builder.Services.AddChatTelemetry();
telemetry.RegisterSecret(labSettings.OpenAI.ApiKey);
telemetry.RegisterSecret(labSettings.OpenAI.GenerationApiKey);
telemetry.RegisterSecret(labSettings.Postgres.ConnectionString);
telemetry.RegisterSecret(new Npgsql.NpgsqlConnectionStringBuilder(labSettings.Postgres.ConnectionString).Password);
builder.Services.AddSingleton(labSettings);
builder.Services.AddSingleton(chatSettings);
builder.Services.AddTransient<ChatProviderTelemetryHandler>();
builder.Services.AddHttpClient("openai", client => client.Timeout = TimeSpan.FromMinutes(4))
    .AddHttpMessageHandler<ChatProviderTelemetryHandler>();
builder.Services.AddSingleton(serviceProvider =>
{
    var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    return new OpenAiApiClient(factory.CreateClient("openai"), labSettings.OpenAI);
});
builder.Services.AddSingleton(serviceProvider =>
{
    // По един генеративен клиент на provider: агентите избират кой да ползват
    // (agents.provider в базата). LiteLLM говори /chat/completions (бекендите
    // му не поддържат /responses), OpenAI — Responses API. Embedding моделът
    // за търсенето винаги остава директно към OpenAI.
    var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    var generationSettings = labSettings.OpenAI with { MaxOutputTokens = chatSettings.MaxOutputTokens };
    var clients = new Dictionary<string, ChatGenerationClient>(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = new(
            factory.CreateClient("openai"),
            generationSettings,
            useChatCompletions: false,
            "OpenAI", telemetry)
    };

    var liteLlm = ChatSettingsLoader.LoadLiteLlm(root);
    if (liteLlm is not null)
    {
        telemetry.RegisterSecret(liteLlm.Value.ApiKey);
        clients["litellm"] = new ChatGenerationClient(
            factory.CreateClient("openai"),
            generationSettings with { ApiKey = liteLlm.Value.ApiKey, BaseUrl = liteLlm.Value.BaseUrl },
            useChatCompletions: true,
            "LiteLLM", telemetry);
    }
    else if (string.Equals(chatSettings.Provider, "litellm", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "Chat:Provider е \"litellm\", но липсва LiteLLM конфигурация. " +
            "Задайте SEARCHQUALITY_LITELLM_BASE_URL/API_KEY или секция LiteLLM в appsettings.Local.json.");
    }

    var defaultProvider = clients.ContainsKey(chatSettings.Provider) ? chatSettings.Provider : "openai";
    return new GenerationClientResolver(clients, defaultProvider);
});
builder.Services.AddSingleton<ChatArchiveStore>();
builder.Services.AddSingleton<ChatService>();

// Telegram каналът е по избор: има токен → ботът тръгва; няма → нищо не се
// променя. Токенът е тайна и се чете от env или гитигнорирания Local файл.
var telegramToken = ChatSettingsLoader.LoadTelegramBotToken(root);
if (telegramToken is not null)
{
    telemetry.RegisterSecret(telegramToken);
    builder.Services.AddHttpClient("telegram", client => client.Timeout = TimeSpan.FromSeconds(70));
    builder.Services.AddSingleton(serviceProvider => new SearchQuality.Chat.Telegram.TelegramApi(
        serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("telegram"),
        telegramToken));
    builder.Services.AddHostedService<SearchQuality.Chat.Telegram.TelegramBotService>();
}

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler(errorApplication =>
{
    errorApplication.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var statusCode = exception switch
        {
            ArgumentException => StatusCodes.Status400BadRequest,
            InvalidOperationException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };
        var detail = statusCode == StatusCodes.Status500InternalServerError
            ? "Възникна неочаквана грешка. Вижте сървърния лог за подробности."
            : exception?.Message;

        context.Response.StatusCode = statusCode;
        await Results.Problem(statusCode: statusCode, title: "Грешка", detail: detail)
            .ExecuteAsync(context);
    });
});

// Само локален достъп + Host header защита — същият модел като лабораторията.
app.Use(async (context, next) =>
{
    if (!networkAccess.IsAllowed(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            title = "Само локален достъп",
            detail = "Приложението е без вход и приема връзки само от този компютър."
        });
        return;
    }

    context.Request.Headers.Remove(ChatNetworkAccess.ProxyHeaderName);

    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // HTML-ът носи ?v= версиите на ресурсите — той трябва ВИНАГИ да се
    // ревалидира (иначе браузърът кешира стар HTML със стари връзки и
    // cache-busting-ът е безполезен). Версионираните css/js могат да се
    // кешират дълго: нова версия = нов URL.
    OnPrepareResponse = context =>
    {
        var isVersionedAsset = context.Context.Request.Query.ContainsKey("v");
        context.Context.Response.Headers.CacheControl = isVersionedAsset
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    }
});

var api = app.MapGroup("/api/chat");

api.MapGet("/profiles", (ChatSettings settings) => Results.Ok(new
{
    users = settings.Users,
    defaultUserId = settings.Users.FirstOrDefault()?.Id
}));

api.MapGet("/state", async (
    string? user,
    ChatService chat,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    var userId = ResolveUser(user, settings);
    return userId is null
        ? UnknownUser()
        : Results.Ok(await chat.GetStateAsync(userId, cancellationToken));
});

api.MapPost("/message", async (
    SendMessageRequest request,
    HttpRequest httpRequest,
    ChatService chat,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedWebRequest(httpRequest))
    {
        return ForbiddenRequest();
    }

    var text = request.Text?.Trim();
    if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Невалидно съобщение",
            detail: "Съобщението трябва да бъде между 1 и 4000 знака.");
    }

    var userId = ResolveUser(request.UserId, settings);
    if (userId is null)
    {
        return UnknownUser();
    }

    var strategy = NormalizeStrategy(request.Strategy, settings.DefaultStrategy);
    var topK = request.TopK is >= 1 and <= 10 ? request.TopK : settings.TopK;
    return Results.Ok(await chat.SendAsync(userId, text, strategy, topK, cancellationToken));
});

// Поточният вариант на /message: NDJSON редове {"type":"context"|"delta"|"done"|"error", ...}.
// Frontend-ът чете response.body на части и рисува текста, докато пристига.
api.MapPost("/message/stream", async (
    SendMessageRequest request,
    HttpContext httpContext,
    ChatService chat,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    var response = httpContext.Response;
    if (!IsTrustedWebRequest(httpContext.Request))
    {
        response.StatusCode = StatusCodes.Status403Forbidden;
        await response.WriteAsJsonAsync(
            new { title = "Невалидна локална заявка" },
            cancellationToken);
        return;
    }

    var text = request.Text?.Trim();
    if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsJsonAsync(
            new { title = "Невалидно съобщение", detail = "Съобщението трябва да бъде между 1 и 4000 знака." },
            cancellationToken);
        return;
    }

    var userId = ResolveUser(request.UserId, settings);
    if (userId is null)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsJsonAsync(new { title = "Непознат потребител" }, cancellationToken);
        return;
    }

    response.ContentType = "application/x-ndjson; charset=utf-8";
    response.Headers.CacheControl = "no-store";

    async Task WriteEventAsync(object payload)
    {
        await response.WriteAsync(
            JsonSerializer.Serialize(payload, streamJsonOptions) + "\n",
            cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    try
    {
        var strategy = NormalizeStrategy(request.Strategy, settings.DefaultStrategy);
        var topK = request.TopK is >= 1 and <= 10 ? request.TopK : settings.TopK;
        var turn = await chat.SendAsync(
            userId,
            text,
            strategy,
            topK,
            context => WriteEventAsync(new { type = "context", context }),
            delta => WriteEventAsync(new { type = "delta", text = delta }),
            cancellationToken);
        await WriteEventAsync(new { type = "done", turn });
    }
    catch (OperationCanceledException)
    {
        // Браузърът е прекъснал връзката (напр. F5 по средата на потока).
        // Няма на кого да се пише — тихо приключваме; заявката към LLM също
        // е прекъсната през cancellation token-а.
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        if (!httpContext.RequestAborted.IsCancellationRequested)
        {
            try
            {
                await WriteEventAsync(new { type = "error", message = exception.Message });
            }
            catch
            {
                // Връзката е умряла между грешката и опита да я съобщим.
            }
        }
    }
});

api.MapPost("/compact", async (
    CompactRequest request,
    HttpRequest httpRequest,
    ChatService chat,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedWebRequest(httpRequest))
    {
        return ForbiddenRequest();
    }

    var userId = ResolveUser(request.UserId, settings);
    return userId is null
        ? UnknownUser()
        : Results.Ok(new { archived = await chat.CompactAsync(userId, cancellationToken) });
});

api.MapPost("/new", async (
    NewChatRequest request,
    HttpRequest httpRequest,
    ChatService chat,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedWebRequest(httpRequest))
    {
        return ForbiddenRequest();
    }

    var userId = ResolveUser(request.UserId, settings);
    return userId is null
        ? UnknownUser()
        : Results.Ok(new { archived = await chat.StartNewChatAsync(userId, request.Archive, cancellationToken) });
});

api.MapGet("/models", async (
    GenerationClientResolver generation,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    try
    {
        var models = await generation.Resolve(settings.Provider).ListModelsAsync(cancellationToken);
        return Results.Ok(new { models = models.Count > 0 ? models : [settings.Model] });
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        // Списъкът е удобство — при проблем dropdown-ът пада до текущия модел.
        _ = exception;
        return Results.Ok(new { models = new[] { settings.Model } });
    }
});

// --- История: преглед на суровите разговори ---------------------------------

api.MapGet("/history/sessions", async (
    string? user,
    string? q,
    string? status,
    ChatArchiveStore archive,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    var userId = ResolveUser(user, settings);
    return userId is null
        ? UnknownUser()
        : Results.Ok(await archive.ListSessionsAsync(userId, q, status, cancellationToken));
});

api.MapGet("/history/stats", async (
    string? user,
    ChatArchiveStore archive,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    var userId = ResolveUser(user, settings);
    return userId is null
        ? UnknownUser()
        : Results.Ok(await archive.GetHistoryStatsAsync(userId, cancellationToken));
});

api.MapGet("/history/sessions/{id:guid}", async (
    Guid id,
    string? user,
    ChatArchiveStore archive,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    var userId = ResolveUser(user, settings);
    return userId is null
        ? UnknownUser()
        : Results.Ok(await archive.GetSessionMessagesAsync(userId, id, cancellationToken));
});

api.MapGet("/history/chunks/{chunkId}", async (
    string chunkId,
    string? user,
    ChatArchiveStore archive,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    var userId = ResolveUser(user, settings);
    if (userId is null)
    {
        return UnknownUser();
    }

    var content = await archive.GetChunkContentAsync(userId, chunkId, cancellationToken);
    return content is null ? Results.NotFound() : Results.Text(content, "text/plain; charset=utf-8");
});

// --- Агенти и версионирани промптове ---------------------------------------

api.MapGet("/agents", async (ChatArchiveStore archive, CancellationToken cancellationToken) =>
    Results.Ok(await archive.ListAgentsAsync(cancellationToken)));

// Каталогът на инструментите по вид агент — какво вижда моделът, дословно.
api.MapGet("/tools", () => Results.Ok(ChatService.DescribeToolCatalog()));

api.MapGet("/agents/{key}/versions", async (
    string key,
    ChatArchiveStore archive,
    CancellationToken cancellationToken) =>
    Results.Ok(await archive.ListPromptVersionsAsync(key, cancellationToken)));

api.MapPost("/agents/{key}", async (
    string key,
    UpdateAgentRequest request,
    HttpRequest httpRequest,
    ChatArchiveStore archive,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedWebRequest(httpRequest))
    {
        return ForbiddenRequest();
    }

    var updated = await archive.UpdateAgentAsync(
        key,
        string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
        request.Enabled,
        string.IsNullOrWhiteSpace(request.Activation) ? null : request.Activation.Trim(),
        cancellationToken);
    return updated ? Results.Ok(await archive.ListAgentsAsync(cancellationToken)) : Results.NotFound();
});

// Нов специалист: ред в регистъра + v1 на промпта му.
api.MapPost("/agents", async (
    CreateAgentRequest request,
    HttpRequest httpRequest,
    ChatArchiveStore archive,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedWebRequest(httpRequest))
    {
        return ForbiddenRequest();
    }

    var key = request.Key?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(key) || !System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9_-]{1,30}$")
        || string.IsNullOrWhiteSpace(request.DisplayName)
        || string.IsNullOrWhiteSpace(request.Activation)
        || string.IsNullOrWhiteSpace(request.Instructions))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Невалиден агент",
            detail: "Ключ (латиница, 2-31 знака), име, „какво поемам“ и начален промпт са задължителни.");
    }

    if ((await archive.ListAgentsAsync(cancellationToken)).Any(a =>
            string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Агентът съществува",
            detail: $"Ключът „{key}“ вече е зает.");
    }

    await archive.CreateAgentAsync(
        key,
        request.DisplayName.Trim(),
        request.Activation.Trim(),
        string.IsNullOrWhiteSpace(request.Model) ? settings.Model : request.Model.Trim(),
        request.Instructions.Trim(),
        cancellationToken);
    return Results.Ok(await archive.ListAgentsAsync(cancellationToken));
});

// Ръчното „Върни към Асистента“ — аварийният изход от специалист.
api.MapPost("/handback", async (
    HandbackRequest request,
    HttpRequest httpRequest,
    ChatService chat,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedWebRequest(httpRequest))
    {
        return ForbiddenRequest();
    }

    var userId = ResolveUser(request.UserId, settings);
    return userId is null
        ? UnknownUser()
        : Results.Ok(new { returnedFrom = await chat.HandbackAsync(userId, cancellationToken) });
});

api.MapPost("/agents/{key}/versions", async (
    string key,
    CreateVersionRequest request,
    HttpRequest httpRequest,
    ChatArchiveStore archive,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedWebRequest(httpRequest))
    {
        return ForbiddenRequest();
    }

    var instructions = request.Instructions?.Trim();
    if (string.IsNullOrWhiteSpace(instructions) || instructions.Length > 20000)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Невалиден промпт",
            detail: "Промптът трябва да бъде между 1 и 20 000 знака.");
    }

    var version = await archive.CreatePromptVersionAsync(
        key,
        instructions,
        string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
        request.Activate,
        cancellationToken);
    return Results.Ok(version);
});

api.MapPost("/agents/{key}/activate", async (
    string key,
    ActivateVersionRequest request,
    HttpRequest httpRequest,
    ChatArchiveStore archive,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedWebRequest(httpRequest))
    {
        return ForbiddenRequest();
    }

    var activated = await archive.ActivatePromptVersionAsync(key, request.Version, cancellationToken);
    return activated ? Results.Ok() : Results.NotFound();
});

api.MapPost("/settings", async (
    UpdateSettingsRequest request,
    HttpRequest httpRequest,
    ChatService chat,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedWebRequest(httpRequest))
    {
        return ForbiddenRequest();
    }

    if (request.ContextTokenBudget is < 1000 or > 500000)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Невалиден бюджет",
            detail: "Контекстният бюджет трябва да бъде между 1 000 и 500 000 токена.");
    }

    var runtime = await chat.UpdateSettingsAsync(
        request.Model,
        request.ContextTokenBudget,
        cancellationToken);
    return Results.Ok(runtime);
});

api.MapGet("/archives", async (
    string? user,
    ChatArchiveStore archive,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    var userId = ResolveUser(user, settings);
    return userId is null
        ? UnknownUser()
        : Results.Ok(await archive.ListArchivesAsync(userId, cancellationToken));
});

api.MapGet("/archives/{id}", async (
    string id,
    string? user,
    ChatArchiveStore archive,
    ChatSettings settings,
    CancellationToken cancellationToken) =>
{
    var userId = ResolveUser(user, settings);
    if (userId is null)
    {
        return UnknownUser();
    }

    var content = await archive.GetArchiveContentAsync(userId, id, cancellationToken);
    return content is null ? Results.NotFound() : Results.Text(content, "text/markdown; charset=utf-8");
});

app.MapFallbackToFile("index.html");

app.Run();

static string? ResolveUser(string? requested, ChatSettings settings) =>
    settings.Users
        .FirstOrDefault(user => string.Equals(user.Id, requested?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?.Id;

static IResult UnknownUser() => Results.Problem(
    statusCode: StatusCodes.Status400BadRequest,
    title: "Непознат потребител",
    detail: "Избери валиден профил от менюто.");

static string NormalizeStrategy(string? requested, string fallback)
{
    string[] known = ["postgres-hybrid", "postgres-pgvector", "postgres-full-text"];
    return known.FirstOrDefault(name =>
            string.Equals(name, requested?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? fallback;
}

static bool IsTrustedWebRequest(HttpRequest request) =>
    string.Equals(request.Headers["X-Search-Lab"], "1", StringComparison.Ordinal);

static IResult ForbiddenRequest() => Results.Problem(
    statusCode: StatusCodes.Status403Forbidden,
    title: "Невалидна локална заявка",
    detail: "Опреснете страницата и опитайте отново.");

public partial class Program;
