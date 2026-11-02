using System.Globalization;
using System.Text.Json;
using SearchQuality.OpenAI;

namespace SearchQuality.Chat;

/// <summary>
/// Инструментите за личните данни на потребителя: артефакти (програма,
/// режим — записан резултат с версии), бележки около тях и дневникът за
/// статистика. Дефинициите и разчитането на аргументите са тук, за да са
/// тествани отделно; изпълнението (SQL-ът) е в ChatArchiveStore, а
/// закачането към агентите — в ChatService.
/// </summary>
public static class PersonalDataTools
{
    /// <summary>Достъпни и за оркестратора, и за специалистите.</summary>
    public static readonly ToolDefinition[] Shared =
    [
        new("get_deliverable",
            "Връща текущия записан документ на потребителя от даден вид (програма, хранителен-режим...) дословно, с бележките към него.",
            """{"type":"object","properties":{"kind":{"type":"string","description":"видът, напр. 'програма'"}},"required":["kind"]}"""),
        new("add_note",
            "Записва бележка около документ (наблюдение, оплакване, предпочитание). Не променя самия документ.",
            """{"type":"object","properties":{"kind":{"type":"string"},"text":{"type":"string","description":"бележката"}},"required":["kind","text"]}"""),
        new("log_activity",
            "Записва едно реално извършено нещо. Използвай точния вид и пълния етикет от думите на потребителя. В data записвай само изрично дадените стойности; подробната разбивка остава в note. За минал ден подай happened_on във формат ГГГГ-ММ-ДД. При корекция първо прочети дневника и подай replaces_id. allow_duplicate е само за изрично второ изпълнение в същия ден.",
            """{"type":"object","properties":{"kind":{"type":"string","description":"вид запис: използвай точно изрично назования от потребителя вид; иначе напр. 'упражнение', 'хранене', 'тегло'"},"label":{"type":"string","description":"за упражнение: дословното пълно име с уточнения като 'wide grip'/'rope'; за неупражнение: кратко име на събитието/продукта, а подробностите са в note"},"data":{"type":"object","description":"единични числа; серии = общият брой, повторения/тегло_кг = топ сетът: '140/160/180/200x12' → {\"серии\":4,\"повторения\":12,\"тегло_кг\":200}; '1x15 + 3x12 @30' → {\"серии\":4,\"повторения\":12,\"тегло_кг\":30}; НЕ масиви"},"note":{"type":"string","description":"пълна разбивка/описание; напр. сетове или подробности за храненето"},"happened_on":{"type":"string","description":"дата ГГГГ-ММ-ДД — САМО когато отчетеното е от минал ден; пропусни за днес"},"replaces_id":{"type":"integer","description":"при корекция: id на записа, който този заменя (от get_log)"},"allow_duplicate":{"type":"boolean","description":"true САМО при изрично казано второ изпълнение в същия ден; НИКОГА при сверка на приключване"}},"required":["kind","label"]}"""),
        new("log_activities",
            "Атомарно записва 1–20 реално извършени неща. Използвай една такава операция, когато потребителят отчете няколко записа. За всеки entry важат същите правила като при log_activity. Ако един entry е невалиден или дубликат, не се записва нищо.",
            """{"type":"object","properties":{"entries":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object","properties":{"kind":{"type":"string","description":"точният вид на записа"},"label":{"type":"string","description":"за упражнение: дословното пълно име с уточненията; за неупражнение: кратко име, подробностите са в note"},"data":{"type":"object","description":"единични числа или статус; не масиви"},"note":{"type":"string","description":"пълна текстова разбивка/описание"},"happened_on":{"type":"string","description":"ГГГГ-ММ-ДД само за минал ден"},"replaces_id":{"type":"integer","description":"id на коригиран запис"},"allow_duplicate":{"type":"boolean","description":"true само при изрично второ изпълнение"}},"required":["kind","label"]}}},"required":["entries"]}"""),
        new("void_activity",
            "Анулира погрешен дневников запис по id, без да го изтрива от одитната история. Използвай след get_log, когато потребителят изрично каже, че даден запис изобщо не е трябвало да съществува. За промяна на стойности използвай log_activity с replaces_id.",
            """{"type":"object","properties":{"id":{"type":"integer","description":"id на погрешния активен запис от get_log"},"reason":{"type":"string","description":"кратка причина, напр. 'потребителят не е посочил упражнение'"}},"required":["id","reason"]}"""),
        new("get_log",
            "Последните записи от дневника (с id-та — нужни за корекция с replaces_id или void_activity). При въпрос за конкретен ден подай happened_on.",
            """{"type":"object","properties":{"kind":{"type":"string","description":"филтър по вид; празно = всички"},"days":{"type":"integer","description":"период назад, по подразбиране 14"},"happened_on":{"type":"string","description":"точен ден ГГГГ-ММ-ДД; използвай при 'днес', 'вчера' или конкретна дата"}},"required":[]}"""),
        new("get_progress",
            "Прогрес от дневника, сметнат детерминистично: за всяка седмица и всяко нещо — min/среднo/max на числовите полета. Ползвай числата, не смятай сам. happened_on фокусира върху етикетите, отчетени в точния ден; за седмица/период го пропусни.",
            """{"type":"object","properties":{"kind":{"type":"string"},"days":{"type":"integer","description":"период назад, по подразбиране 60"},"happened_on":{"type":"string","description":"точен ден ГГГГ-ММ-ДД; използвай само при изрично искане за един ден, не при седмица/период"}},"required":["kind"]}""")
    ];

    /// <summary>Само за специалисти: готовият резултат става записан документ (нова версия).</summary>
    public static readonly ToolDefinition SaveDeliverable =
        new("save_deliverable",
            "Създава изцяло нов постоянен документ само ако потребителят още няма документ от този вид. Ако вече има такъв, операцията се отказва: използвай edit_deliverable за малка промяна или replace_deliverable за изрично поискана пълна замяна. content е целият нов документ.",
            """{"type":"object","properties":{"kind":{"type":"string","description":"видът, напр. 'програма'"},"title":{"type":"string","description":"кратко заглавие"},"content":{"type":"string","description":"ПЪЛНОТО съдържание в Markdown — целият документ, не резюме"}},"required":["kind","title","content"]}""");

    /// <summary>Само за специалисти: умишлена пълна замяна с optimistic lock.</summary>
    public static readonly ToolDefinition ReplaceDeliverable =
        new("replace_deliverable",
            "Заменя целия текущ документ с нова версия. Първо извикай get_deliverable и подай върнатия current_document_id; ако междувременно текущата версия е различна, операцията се отказва. Използвай само когато потребителят изрично иска пълна преработка, не за локална промяна.",
            """{"type":"object","properties":{"kind":{"type":"string","description":"видът на текущия документ"},"current_document_id":{"type":"string","description":"document_id от непосредствено предходния get_deliverable"},"title":{"type":"string","description":"заглавие на новата версия"},"content":{"type":"string","description":"пълното ново съдържание в Markdown"}},"required":["kind","current_document_id","title","content"]}""");

    /// <summary>Само за специалисти: атомарна точна редакция на текущ документ.</summary>
    public static readonly ToolDefinition EditDeliverable =
        new("edit_deliverable",
            "Прави една или повече малки точни промени по текущ документ и записва нова версия. Първо прочети документа с get_deliverable. old_text трябва да е дословен и уникален откъс; new_text е точният заместител. Празен new_text премахва съдържащия ред. Всички промени са атомарни: при липсващ или неуникален old_text не се записва нищо. Сложи всички промени в едно извикване.",
            """{"type":"object","properties":{"kind":{"type":"string","description":"видът на текущия документ, напр. 'програма'"},"title":{"type":"string","description":"незадължително ново заглавие на документа"},"edits":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object","properties":{"old_text":{"type":"string","description":"дословен уникален откъс от текущия документ"},"new_text":{"type":"string","description":"точният заместител; празен низ изтрива целия съдържащ ред/редове"}},"required":["old_text","new_text"]}}},"required":["kind","edits"]}""");

    public sealed record SaveArgs(string Kind, string Title, string Content);

    public sealed record ReplaceArgs(
        string Kind,
        string CurrentDocumentId,
        string Title,
        string Content);

    public sealed record TextEdit(string OldText, string NewText);

    public sealed record EditArgs(string Kind, string? Title, IReadOnlyList<TextEdit> Edits);

    public sealed record LogArgs(
        string Kind,
        string Label,
        string? DataJson,
        string? Note,
        long? ReplacesId,
        bool AllowDuplicate,
        DateOnly? HappenedOn,
        string? HappenedOnRaw,
        bool HappenedOnProvided);

    public sealed record VoidArgs(long Id, string Reason);

    public sealed record LogQuery(
        string? Kind,
        int Days,
        DateOnly? HappenedOn,
        string? HappenedOnRaw,
        bool HappenedOnProvided);

    public static SaveArgs? ParseSave(string argumentsJson)
    {
        using var document = SafeParse(argumentsJson);
        if (document is null)
        {
            return null;
        }

        var kind = GetString(document.RootElement, "kind");
        var title = GetString(document.RootElement, "title");
        var content = GetString(document.RootElement, "content");
        return kind is null || content is null
            ? null
            : new SaveArgs(Normalize(kind), title ?? kind, content);
    }

    public static ReplaceArgs? ParseReplace(string argumentsJson)
    {
        using var document = SafeParse(argumentsJson);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        var kind = GetString(root, "kind");
        var currentDocumentId = GetString(root, "current_document_id");
        var title = GetString(root, "title");
        var content = GetString(root, "content");
        return kind is null || currentDocumentId is null || content is null
            ? null
            : new ReplaceArgs(
                Normalize(kind),
                currentDocumentId.Trim(),
                title ?? kind,
                content);
    }

    public static EditArgs? ParseEdit(string argumentsJson)
    {
        using var document = SafeParse(argumentsJson);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        var kind = GetString(root, "kind");
        if (kind is null
            || !root.TryGetProperty("edits", out var editsElement)
            || editsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var edits = new List<TextEdit>();
        foreach (var element in editsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || GetString(element, "old_text") is not { } oldText
                || !element.TryGetProperty("new_text", out var newTextElement)
                || newTextElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            edits.Add(new TextEdit(oldText, newTextElement.GetString() ?? ""));
            if (edits.Count > 20)
            {
                return null;
            }
        }

        return edits.Count == 0
            ? null
            : new EditArgs(Normalize(kind), GetString(root, "title"), edits);
    }

    /// <summary>
    /// Прилага редакциите върху локално копие. Така нито една частична
    /// промяна не достига до хранилището при липсващ или двусмислен откъс.
    /// </summary>
    public static (string? Content, string? Error) ApplyExactEdits(
        string content,
        IReadOnlyList<TextEdit> edits)
    {
        var updated = content;
        foreach (var edit in edits)
        {
            var occurrences = CountOccurrences(updated, edit.OldText);
            if (occurrences == 0)
            {
                return (null, "old_text не е намерен дословно в текущия документ");
            }

            if (occurrences > 1)
            {
                return (null, "old_text се среща повече от веднъж; подай по-дълъг уникален откъс");
            }

            var matchIndex = updated.IndexOf(edit.OldText, StringComparison.Ordinal);
            updated = edit.NewText.Length == 0
                ? RemoveContainingLines(updated, matchIndex, edit.OldText.Length)
                : updated.Replace(edit.OldText, edit.NewText, StringComparison.Ordinal);
        }

        return (updated, null);
    }

    public static (string Kind, string Text)? ParseNote(string argumentsJson)
    {
        using var document = SafeParse(argumentsJson);
        var kind = document is null ? null : GetString(document.RootElement, "kind");
        var text = document is null ? null : GetString(document.RootElement, "text");
        return kind is null || text is null ? null : (Normalize(kind), text);
    }

    public static LogArgs? ParseLog(string argumentsJson)
    {
        using var document = SafeParse(argumentsJson);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        var kind = GetString(root, "kind");
        var label = GetString(root, "label");
        if (kind is null || label is null)
        {
            return null;
        }

        string? dataJson = null;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            dataJson = data.GetRawText();
        }

        long? replacesId = null;
        if (root.TryGetProperty("replaces_id", out var replaces) && replaces.ValueKind == JsonValueKind.Number)
        {
            replacesId = replaces.GetInt64();
        }

        var allowDuplicate = root.TryGetProperty("allow_duplicate", out var duplicate)
            && duplicate.ValueKind == JsonValueKind.True;

        // Суровата стойност се пази отделно: неразбираема дата трябва да
        // ВЪРНЕ грешка на модела, не тихо да стане „днес" и да поврежда
        // хронологията. Отказът е у извикващия (ChatService).
        var happenedOnProvided = root.TryGetProperty("happened_on", out var happenedOnValue)
            && happenedOnValue.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        var happenedOnRaw = happenedOnProvided
            ? happenedOnValue.ValueKind == JsonValueKind.String
                ? happenedOnValue.GetString()
                : happenedOnValue.GetRawText()
            : null;
        DateOnly? happenedOn = null;
        if (happenedOnProvided
            && happenedOnValue.ValueKind == JsonValueKind.String
            && happenedOnRaw is not null
            && DateOnly.TryParseExact(
                happenedOnRaw.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            happenedOn = parsedDate;
        }

        return new LogArgs(
            Normalize(kind), label.Trim(), dataJson, GetString(root, "note"),
            replacesId, allowDuplicate, happenedOn, happenedOnRaw, happenedOnProvided);
    }

    public static IReadOnlyList<LogArgs>? ParseLogs(string argumentsJson)
    {
        using var document = SafeParse(argumentsJson);
        if (document is null
            || !document.RootElement.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<LogArgs>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || ParseLog(entry.GetRawText()) is not { } parsed)
            {
                return null;
            }

            result.Add(parsed);
            if (result.Count > 20)
            {
                return null;
            }
        }

        return result.Count == 0 ? null : result;
    }

    public static VoidArgs? ParseVoid(string argumentsJson)
    {
        using var document = SafeParse(argumentsJson);
        if (document is null
            || !document.RootElement.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt64(out var parsedId)
            || parsedId <= 0
            || GetString(document.RootElement, "reason") is not { } reason)
        {
            return null;
        }

        return new VoidArgs(parsedId, reason.Trim());
    }

    public static LogQuery ParseLogQuery(string argumentsJson, int defaultDays)
    {
        using var document = SafeParse(argumentsJson);
        if (document is null)
        {
            return new LogQuery(null, defaultDays, null, null, false);
        }

        var kind = GetString(document.RootElement, "kind");
        var days = document.RootElement.TryGetProperty("days", out var value)
            && value.ValueKind == JsonValueKind.Number
                ? Math.Clamp(value.GetInt32(), 1, 365)
                : defaultDays;
        var happenedOnProvided = document.RootElement.TryGetProperty(
            "happened_on",
            out var happenedOnValue)
            && happenedOnValue.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        var happenedOnRaw = happenedOnProvided
            ? happenedOnValue.ValueKind == JsonValueKind.String
                ? happenedOnValue.GetString()
                : happenedOnValue.GetRawText()
            : null;
        DateOnly? happenedOn = null;
        if (happenedOnRaw is not null
            && DateOnly.TryParseExact(
                happenedOnRaw.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
        {
            happenedOn = parsedDate;
        }

        return new LogQuery(
            kind is null ? null : Normalize(kind),
            days,
            happenedOn,
            happenedOnRaw,
            happenedOnProvided);
    }

    public static string? ParseKind(string argumentsJson)
    {
        using var document = SafeParse(argumentsJson);
        var kind = document is null ? null : GetString(document.RootElement, "kind");
        return kind is null ? null : Normalize(kind);
    }

    /// <summary>Видът е ключ — малки букви, без излишни интервали, за да не се
    /// раждат близнаци като 'Програма' и 'програма '.</summary>
    public static string Normalize(string kind) =>
        kind.Trim().ToLowerInvariant().Replace(' ', '-');

    private static JsonDocument? SafeParse(string json)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static string RemoveContainingLines(string value, int matchIndex, int matchLength)
    {
        var lineStart = matchIndex == 0
            ? 0
            : value.LastIndexOf('\n', matchIndex - 1) + 1;
        var matchEnd = matchIndex + matchLength;
        var lineBreak = matchEnd > 0 && value[matchEnd - 1] == '\n'
            ? matchEnd - 1
            : value.IndexOf('\n', matchEnd);
        var lineEnd = lineBreak < 0 ? value.Length : lineBreak + 1;
        return value.Remove(lineStart, lineEnd - lineStart);
    }
}
