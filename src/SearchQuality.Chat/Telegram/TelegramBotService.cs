using System.Text;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;

namespace SearchQuality.Chat.Telegram;

/// <summary>
/// Telegram входът към чата. Тънък адаптер: превежда съобщенията от Telegram
/// към <see cref="ChatService"/> и отговорите обратно — цялата логика (агенти,
/// памет, история, компактиране) си остава в ChatService и работи еднакво
/// за всички канали. Стриймът се имитира с редакции на съобщението (~1/сек);
/// междинните редакции са чист текст, финалната е форматиран Telegram HTML.
/// </summary>
public sealed class TelegramBotService(
    TelegramApi api,
    ChatService chat,
    ChatArchiveStore archive,
    ChatSettings settings,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private const string Channel = "telegram";
    private static readonly TimeSpan EditInterval = TimeSpan.FromSeconds(1.2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram ботът е стартиран (long polling).");

        // Offset-ът се пази в базата: рестарт между обработката и следващия
        // poll иначе кара Telegram да достави същите съобщения втори път
        // (дублиран ход, дублирани записи). +1 се записва ПРЕДИ обработката —
        // по-добре да изпуснем ход при crash, отколкото да го изпълним двойно.
        var offset = await archive.GetStateValueAsync("telegram_offset", stoppingToken) is { } saved
            && long.TryParse(saved, out var parsed) ? parsed : 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await api.GetUpdatesAsync(offset, timeoutSeconds: 50, stoppingToken);
                if (updates.Count > 0)
                {
                    offset = updates.Max(u => u.GetProperty("update_id").GetInt64()) + 1;
                    await archive.SetStateValueAsync(
                        "telegram_offset", offset.ToString(), stoppingToken);
                }

                foreach (var update in updates)
                {
                    // Всеки update се обработва самостоятелно: един дълъг ход
                    // (LLM + инструменти, минути) не бива да блокира бота за
                    // останалите потребители. Редът на един потребител се
                    // пази от TurnLock-а на сесията му в ChatService.
                    var pinned = update;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleUpdateAsync(pinned, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            // спиране на сервиза по средата на ход — тихо
                        }
                        catch (Exception exception)
                        {
                            logger.LogError(exception, "Грешка при обработка на Telegram update.");
                        }
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Истинско спиране на сервиза. ВНИМАНИЕ: HttpClient timeout
                // хвърля TaskCanceledException (наследник на OCE) — гол
                // catch тук „излизаше" тихо от цикъла при мрежов хълцук и
                // ботът умираше завинаги при жив сервиз.
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Telegram poll грешка — пауза и нов опит.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleUpdateAsync(JsonElement update, CancellationToken cancellationToken)
    {
        // Натискане на бутон от списъка с потребители → свързване.
        if (update.TryGetProperty("callback_query", out var callback))
        {
            await HandleLinkButtonAsync(callback, cancellationToken);
            return;
        }

        if (!update.TryGetProperty("message", out var message)
            || !message.TryGetProperty("text", out var textElement))
        {
            return;
        }

        var chatId = message.GetProperty("chat").GetProperty("id").GetInt64();
        var text = textElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var userId = await archive.GetChannelUserAsync(Channel, chatId.ToString(), cancellationToken);

        switch (text.Split(' ')[0].ToLowerInvariant())
        {
            case "/start" or "/link":
                await SendUserPickerAsync(chatId, userId, cancellationToken);
                return;

            case "/unlink":
                await archive.UnlinkChannelUserAsync(Channel, chatId.ToString(), cancellationToken);
                await api.SendMessageAsync(chatId, "Връзката е премахната. /start — за ново свързване.", html: false, replyMarkup: null, cancellationToken);
                return;

            case "/new":
                if (userId is not null)
                {
                    await chat.StartNewChatAsync(userId, archiveCurrent: true, cancellationToken);
                    await api.SendMessageAsync(chatId, "Нов разговор — старият е архивиран в паметта.", html: false, replyMarkup: null, cancellationToken);
                }

                return;

            case "/back":
                if (userId is not null)
                {
                    var previous = await chat.HandbackAsync(userId, cancellationToken);
                    await api.SendMessageAsync(
                        chatId,
                        previous is null ? "Микрофонът и без това е у Асистента." : "Върнах разговора на Асистента.",
                        html: false, replyMarkup: null, cancellationToken);
                }

                return;
        }

        if (userId is null)
        {
            await SendUserPickerAsync(chatId, linkedUserId: null, cancellationToken);
            return;
        }

        await RunTurnAsync(chatId, userId, text, cancellationToken);
    }

    /// <summary>Едно завъртане: placeholder → редакции на ~1 сек → финален HTML.</summary>
    private async Task RunTurnAsync(long chatId, string userId, string text, CancellationToken cancellationToken)
    {
        try
        {
            await api.SendChatActionAsync(chatId, cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Telegram typing индикаторът не беше доставен.");
        }

        long messageId = 0;
        var streamed = new StringBuilder();
        var lastEdit = DateTimeOffset.MinValue;

        // Делтите идват последователно от стрийма — без нужда от заключване.
        // Прогрес редакциите са козметика: провал (напр. 429 flood control)
        // НЕ бива да гърми нагоре — иначе убива целия ход по средата, след
        // като инструментите вече са записали странични ефекти.
        async Task OnDelta(string piece)
        {
            streamed.Append(piece);
            var now = DateTimeOffset.UtcNow;
            if (now - lastEdit < EditInterval)
            {
                return;
            }

            lastEdit = now;
            try
            {
                var preview = Truncate(streamed.ToString(), TelegramMarkdown.ChunkLength) + " …";
                if (messageId == 0)
                {
                    messageId = await api.SendMessageAsync(chatId, preview, html: false, replyMarkup: null, cancellationToken);
                }
                else
                {
                    await api.EditMessageTextAsync(chatId, messageId, preview, html: false, cancellationToken);
                }
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Прогрес редакцията пропадна — продължаваме без нея.");
            }
        }

        ChatTurnResponse turn;
        try
        {
            turn = await chat.SendAsync(
                userId, text, settings.DefaultStrategy, settings.TopK,
                onContext: null, OnDelta, cancellationToken, channel: "telegram");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Грешка в чат хода за Telegram потребител {UserId}.", userId);
            const string error =
                "⚠ Не успях да завърша отговора. Опитай отново след малко.";
            await TryDeliverAsync(
                chatId,
                messageId,
                error,
                html: false,
                chunkIndex: -1,
                cancellationToken);

            return;
        }

        await DeliverAnswerAsync(chatId, messageId, turn, cancellationToken);
    }

    /// <summary>Финалният отговор: форматиран, на парчета под лимита, с бележките на екипа.</summary>
    private async Task DeliverAnswerAsync(
        long chatId, long placeholderId, ChatTurnResponse turn, CancellationToken cancellationToken)
    {
        // Истината от дневника е първа и е отделна от свободния LLM текст.
        // Ако има streaming placeholder, той се превръща точно в това
        // потвърждение и никога не остава със старото многоточие.
        var confirmationChunks = TelegramMarkdown.ToDiaryConfirmationChunks(turn.DiaryConfirmations);
        foreach (var confirmation in confirmationChunks)
        {
            var delivered = await TryDeliverAsync(
                chatId,
                placeholderId,
                confirmation,
                html: true,
                chunkIndex: -1,
                cancellationToken);
            if (placeholderId != 0)
            {
                if (!delivered)
                {
                    await ReplacePreviewWithFailureAsync(
                        chatId,
                        placeholderId,
                        cancellationToken);
                }

                placeholderId = 0;
            }
        }

        // Кой говори — само когато не е основният асистент (handoff към специалист).
        var speaker = turn.AgentKey != "chat" && !string.IsNullOrWhiteSpace(turn.ActiveAgentName ?? turn.AgentKey)
            ? $"<b>🎤 {TelegramMarkdown.Escape(turn.ActiveAgentName ?? turn.AgentKey)}</b>\n"
            : "";

        var chunks = TelegramMarkdown.ToChunks(turn.Answer.Text);
        for (var i = 0; i < chunks.Count; i++)
        {
            var body = (i == 0 ? speaker : "") + chunks[i];
            if (body.Length > 4096)
            {
                await TryDeliverAsync(
                    chatId,
                    placeholderId,
                    speaker.TrimEnd(),
                    html: true,
                    chunkIndex: i,
                    cancellationToken);
                placeholderId = 0;
                body = chunks[i];
            }

            var delivered = await TryDeliverAsync(
                chatId,
                i == 0 ? placeholderId : 0,
                body,
                html: true,
                chunkIndex: i,
                cancellationToken);
            if (i == 0 && placeholderId != 0)
            {
                if (!delivered)
                {
                    await ReplacePreviewWithFailureAsync(
                        chatId,
                        placeholderId,
                        cancellationToken);
                }

                placeholderId = 0;
            }
        }

        // Системните бележки (handoff, връщане) — както разделителите в уеб UI,
        // но КРАТКИ: пълният текст на предаването (цялата задача към
        // специалиста) е за одита в Историята, не за чата на потребителя.
        foreach (var note in turn.Notes)
        {
            await TryDeliverAsync(
                chatId,
                placeholderId: 0,
                $"<i>{TelegramMarkdown.Escape(CompactNote(note))}</i>",
                html: true,
                chunkIndex: -1,
                cancellationToken);
        }
    }

    private async Task<bool> TryDeliverAsync(
        long chatId,
        long placeholderId,
        string text,
        bool html,
        int chunkIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            if (placeholderId != 0)
            {
                await api.EditMessageTextAsync(
                    chatId,
                    placeholderId,
                    text,
                    html,
                    cancellationToken);
            }
            else
            {
                await api.SendMessageAsync(
                    chatId,
                    text,
                    html,
                    replyMarkup: null,
                    cancellationToken);
            }

            return true;
        }
        catch (Exception exception) when (html && IsContentRejection(exception))
        {
            logger.LogWarning(
                exception,
                "Telegram отхвърли HTML парче {Index} — пращам чист текст.",
                chunkIndex);
            var plain = HtmlToPlain(text);
            try
            {
                if (placeholderId != 0)
                {
                    await api.EditMessageTextAsync(
                        chatId,
                        placeholderId,
                        plain,
                        html: false,
                        cancellationToken);
                }
                else
                {
                    await api.SendMessageAsync(
                        chatId,
                        plain,
                        html: false,
                        replyMarkup: null,
                        cancellationToken);
                }

                return true;
            }
            catch (Exception fallbackException) when (
                fallbackException is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(
                    fallbackException,
                    "И чистият текст пропадна за парче {Index}.",
                    chunkIndex);
                return false;
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                exception,
                "Telegram не достави текстово парче {Index}.",
                chunkIndex);
            return false;
        }
    }

    /// <summary>
    /// Само категоричен отказ на съдържанието (HTTP 400, напр. невалидни
    /// entities) позволява plain преизпращане. При timeout/мрежова грешка
    /// оригиналът може вече да е доставен — повторение би дублирало
    /// съобщението при потребителя.
    /// </summary>
    public static bool IsContentRejection(Exception exception) =>
        exception is TelegramApiException { ErrorCode: 400 };

    private async Task ReplacePreviewWithFailureAsync(
        long chatId,
        long placeholderId,
        CancellationToken cancellationToken)
    {
        try
        {
            await api.EditMessageTextAsync(
                chatId,
                placeholderId,
                "⚠ Финалният отговор не можа да бъде доставен. Опитай отново.",
                html: false,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                exception,
                "Неуспешно изчистване на streaming preview {MessageId}.",
                placeholderId);
        }
    }

    private static string HtmlToPlain(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));

    /// <summary>„— Асистентът предаде разговора на Треньор: {дълга задача} —" → само заглавната част.</summary>
    private static string CompactNote(string note)
    {
        const int limit = 250;
        if (note.Length <= limit)
        {
            return note;
        }

        // Предаването носи цялата формулирана задача — за Telegram стига кой
        // на кого. Резюмето при приключване обаче е ценно — то само се скъсява.
        if (note.Contains("предаде разговора", StringComparison.Ordinal)
            && note.IndexOf(':') is var colon and > 0)
        {
            return note[..colon].TrimEnd() + " —";
        }

        return note[..limit].TrimEnd() + " … —";
    }

    /// <summary>Списък с потребителите като бутони — свързването е един натиск.</summary>
    private async Task SendUserPickerAsync(long chatId, string? linkedUserId, CancellationToken cancellationToken)
    {
        var current = linkedUserId is null
            ? "Не си свързан с потребител."
            : $"В момента си свързан с: {NameOf(linkedUserId)}.";
        var keyboard = new
        {
            inline_keyboard = settings.Users
                .Select(user => new[] { new { text = user.Name, callback_data = $"link:{user.Id}" } })
                .ToArray()
        };
        await api.SendMessageAsync(
            chatId, $"{current}\nИзбери кой си:", html: false, keyboard, cancellationToken);
    }

    private async Task HandleLinkButtonAsync(JsonElement callback, CancellationToken cancellationToken)
    {
        var callbackId = callback.GetProperty("id").GetString()!;
        var chatId = callback.GetProperty("message").GetProperty("chat").GetProperty("id").GetInt64();
        var data = callback.TryGetProperty("data", out var dataElement) ? dataElement.GetString() : null;

        await api.AnswerCallbackQueryAsync(callbackId, cancellationToken);
        if (data?.StartsWith("link:", StringComparison.Ordinal) is not true)
        {
            return;
        }

        var userId = data["link:".Length..];
        if (settings.Users.All(user => user.Id != userId))
        {
            return;
        }

        await archive.LinkChannelUserAsync(Channel, chatId.ToString(), userId, cancellationToken);
        await api.SendMessageAsync(
            chatId,
            $"Готово — ти си {NameOf(userId)}. Паметта и историята са общи с уеб чата. Пиши!",
            html: false, replyMarkup: null, cancellationToken);
    }

    private string NameOf(string userId) =>
        settings.Users.FirstOrDefault(user => user.Id == userId)?.Name ?? userId;

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];
}
