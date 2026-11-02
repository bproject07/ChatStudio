using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SearchQuality.Chat;

namespace SearchQuality.Chat.Telegram;

/// <summary>
/// Markdown → Telegram HTML. Моделите пишат обикновен Markdown; Telegram
/// разбира само шепа тагове (b, i, u, s, a, code, pre, blockquote). Markdig
/// върши парсването, тук е само „шапката": кой блок как се изписва.
/// Нов канал (друг месинджър) = нова такава шапка, парсерът е същият.
/// </summary>
public static class TelegramMarkdown
{
    private const int MaxHtmlChunkLength = 3900;

    /// <summary>Едно потвърждение никога не надхвърля това след рендер —
    /// така група от едно винаги остава под Telegram лимита.</summary>
    private const int MaxConfirmationBlockLength = 3600;

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UsePipeTables().Build();

    /// <summary>Telegram реже на 4096 знака; оставяме резерв за HTML таговете.</summary>
    public const int ChunkLength = 3500;

    /// <summary>
    /// Целият отговор като едно или няколко Telegram HTML парчета.
    /// Реже се по абзаци ОТ Markdown-а (не от HTML-а), за да не се
    /// среже таг наполовина — всяко парче се рендерира самостоятелно.
    /// </summary>
    public static IReadOnlyList<string> ToChunks(string markdown)
    {
        var chunks = new List<string>();
        foreach (var piece in SplitByParagraphs(markdown, ChunkLength))
        {
            AddRenderedChunks(piece, chunks);
        }

        return chunks.Count > 0 ? chunks : [Escape(markdown.Trim())];
    }

    /// <summary>
    /// Видимият, проверим резултат от дневника. Всички стойности от
    /// потребителя се escape-ват; HTML-ът не зависи от текста на модела.
    /// </summary>
    public static string FormatDiaryConfirmations(
        IReadOnlyList<DiaryConfirmation> confirmations)
    {
        if (confirmations.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder("<b>Дневник</b>\n");
        foreach (var confirmation in confirmations)
        {
            builder.Append(FormatConfirmationBlock(confirmation)).Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Потвържденията като едно или няколко парчета под Telegram лимита.
    /// Дели се само между потвърждения, а всяко отделно има твърд собствен
    /// cap, така че и „раздуто" от escape-ване потвърждение остава доставимо.
    /// </summary>
    public static IReadOnlyList<string> ToDiaryConfirmationChunks(
        IReadOnlyList<DiaryConfirmation> confirmations)
    {
        var chunks = new List<string>();
        var group = new List<DiaryConfirmation>();
        foreach (var confirmation in confirmations)
        {
            group.Add(confirmation);
            var candidate = FormatDiaryConfirmations(group);
            if (candidate.Length <= MaxHtmlChunkLength)
            {
                continue;
            }

            group.RemoveAt(group.Count - 1);
            if (group.Count > 0)
            {
                chunks.Add(FormatDiaryConfirmations(group));
            }

            group.Clear();
            group.Add(confirmation);
        }

        if (group.Count > 0)
        {
            chunks.Add(FormatDiaryConfirmations(group));
        }

        return chunks;
    }

    private static string FormatConfirmationBlock(DiaryConfirmation confirmation)
    {
        var (icon, action) = confirmation.Status switch
        {
            DiaryConfirmationStatus.Recorded => ("✅", "Записано"),
            DiaryConfirmationStatus.Corrected => ("✅", "Коригирано"),
            DiaryConfirmationStatus.Voided => ("🗑", "Анулирано"),
            DiaryConfirmationStatus.NotRecorded => ("↔️", "Не е записано"),
            _ => ("⛔", "Нищо не е записано")
        };

        var builder = new StringBuilder();
        builder
            .Append(icon)
            .Append(' ')
            .Append("<b>")
            .Append(action)
            .Append(":</b> ")
            .Append(Escape(Truncate(confirmation.Label, 500)));

        var metadata = new List<string>();
        if (!string.IsNullOrWhiteSpace(confirmation.ProgramDay))
        {
            metadata.Add(Truncate(confirmation.ProgramDay, 150));
        }

        if (!string.IsNullOrWhiteSpace(confirmation.Kind))
        {
            metadata.Add(Truncate(confirmation.Kind, 150));
        }

        if (confirmation.Id is { } id)
        {
            metadata.Add($"id {id}");
        }

        if (metadata.Count > 0)
        {
            builder
                .Append("\n   ")
                .Append(Escape(string.Join(" · ", metadata)));
        }

        if (FormatActivityData(confirmation.DataJson) is { } data)
        {
            builder.Append("\n   ").Append(Escape(data));
        }

        if (!string.IsNullOrWhiteSpace(confirmation.Note))
        {
            builder
                .Append("\n   ")
                .Append(Escape(Truncate(confirmation.Note, 350)));
        }

        if (!string.IsNullOrWhiteSpace(confirmation.Message))
        {
            builder
                .Append("\n   ")
                .Append(Escape(Truncate(confirmation.Message, 500)));
        }

        var block = builder.ToString();
        if (block.Length <= MaxConfirmationBlockLength)
        {
            return block;
        }

        // Escape-ването може да умножи дължината (& → &amp;). Таговете са
        // само в началния префикс, затова срязваме в escape-натия текст и
        // махаме евентуално започнато entity, за да остане валиден HTML.
        var cut = block[..MaxConfirmationBlockLength];
        var ampersand = cut.LastIndexOf('&');
        if (ampersand >= 0 && cut.IndexOf(';', ampersand) < 0)
        {
            cut = cut[..ampersand];
        }

        return cut.TrimEnd() + " …";
    }

    /// <summary>Едно Markdown парче като Telegram HTML. При изненада — escape-нат суров текст.</summary>
    public static string ToHtml(string markdown)
    {
        try
        {
            var document = Markdown.Parse(markdown, Pipeline);
            var sb = new StringBuilder();
            foreach (var block in document)
            {
                RenderBlock(block, sb, listIndent: "");
            }

            return sb.ToString().Trim();
        }
        catch (Exception)
        {
            return Escape(markdown.Trim());
        }
    }

    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static void AddRenderedChunks(string markdown, List<string> chunks)
    {
        var html = ToHtml(markdown);
        if (html.Length <= MaxHtmlChunkLength)
        {
            if (!string.IsNullOrWhiteSpace(html))
            {
                chunks.Add(html);
            }

            return;
        }

        // Escape-ването може да разшири един знак до 5, а Markdown таговете
        // също добавят символи. Делим източника и рендерираме всяка половина
        // самостоятелно, така HTML таг никога не остава срязан.
        var splitAt = FindSafeSplit(markdown);
        AddRenderedChunks(markdown[..splitAt], chunks);
        AddRenderedChunks(markdown[splitAt..], chunks);
    }

    private static int FindSafeSplit(string markdown)
    {
        if (markdown.Length <= 1)
        {
            return 1;
        }

        var middle = markdown.Length / 2;
        for (var index = middle; index > Math.Max(0, middle - 500); index--)
        {
            if (char.IsWhiteSpace(markdown[index]))
            {
                return index + 1;
            }
        }

        return middle;
    }

    private static string? FormatActivityData(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(dataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = document.RootElement
                .EnumerateObject()
                .Take(8)
                .Select(property =>
                {
                    var value = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.GetRawText();
                    return property.Name.ToLowerInvariant() switch
                    {
                        "серии" => $"{value} серии",
                        "повторения" => $"{value} повторения",
                        "тегло_кг" => $"{value} кг",
                        _ => $"{property.Name.Replace('_', ' ')} {value}"
                    };
                });
            var result = string.Join(" · ", values);
            return result.Length == 0 ? null : Truncate(result, 500);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + " …";

    private static IEnumerable<string> SplitByParagraphs(string markdown, int maxLength)
    {
        var current = new StringBuilder();
        foreach (var paragraph in markdown.Replace("\r\n", "\n").Split("\n\n"))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > maxLength)
            {
                yield return current.ToString();
                current.Clear();
            }

            // Единичен абзац над лимита (гигантски код блок) — твърдо срязване.
            if (paragraph.Length > maxLength)
            {
                for (var at = 0; at < paragraph.Length; at += maxLength)
                {
                    yield return paragraph.Substring(at, Math.Min(maxLength, paragraph.Length - at));
                }

                continue;
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(paragraph);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static void RenderBlock(Block block, StringBuilder sb, string listIndent)
    {
        switch (block)
        {
            case HeadingBlock heading:
                sb.Append("<b>");
                RenderInlines(heading.Inline, sb);
                sb.Append("</b>\n\n");
                break;

            case ParagraphBlock paragraph:
                RenderInlines(paragraph.Inline, sb);
                sb.Append("\n\n");
                break;

            case ListBlock list:
                var number = 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var bullet = list.IsOrdered ? $"{number++}." : "•";
                    RenderListItem(item, sb, listIndent, bullet);
                }

                sb.Append('\n');
                break;

            case FencedCodeBlock code:
                RenderCode(code.Lines.ToString(), sb);
                break;

            case CodeBlock code:
                RenderCode(code.Lines.ToString(), sb);
                break;

            case QuoteBlock quote:
                sb.Append("<blockquote>");
                var inner = new StringBuilder();
                foreach (var child in quote)
                {
                    RenderBlock(child, inner, listIndent);
                }

                sb.Append(inner.ToString().Trim());
                sb.Append("</blockquote>\n\n");
                break;

            case Table table:
                RenderTable(table, sb);
                break;

            case ThematicBreakBlock:
                sb.Append("———\n\n");
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    RenderBlock(child, sb, listIndent);
                }

                break;

            case LeafBlock leaf when leaf.Inline is not null:
                RenderInlines(leaf.Inline, sb);
                sb.Append('\n');
                break;
        }
    }

    private static void RenderListItem(ListItemBlock item, StringBuilder sb, string indent, string bullet)
    {
        var first = true;
        foreach (var child in item)
        {
            if (child is ListBlock nested)
            {
                RenderBlock(nested, sb, indent + "   ");
                continue;
            }

            sb.Append(indent);
            sb.Append(first ? $"{bullet} " : "   ");
            first = false;

            if (child is LeafBlock { Inline: not null } leaf)
            {
                RenderInlines(leaf.Inline, sb);
                sb.Append('\n');
            }
            else
            {
                var inner = new StringBuilder();
                RenderBlock(child, inner, indent);
                sb.Append(inner.ToString().Trim());
                sb.Append('\n');
            }
        }
    }

    private static void RenderCode(string code, StringBuilder sb)
    {
        sb.Append("<pre>");
        sb.Append(Escape(code.TrimEnd()));
        sb.Append("</pre>\n\n");
    }

    /// <summary>Telegram няма таблици — моноширинен блок с подравнени колони.</summary>
    private static void RenderTable(Table table, StringBuilder sb)
    {
        var rows = new List<string[]>();
        foreach (var row in table.OfType<TableRow>())
        {
            rows.Add(row.OfType<TableCell>()
                .Select(cell =>
                {
                    var text = new StringBuilder();
                    foreach (var child in cell)
                    {
                        if (child is LeafBlock { Inline: not null } leaf)
                        {
                            RenderPlainInlines(leaf.Inline, text);
                        }
                    }

                    return text.ToString().Trim();
                })
                .ToArray());
        }

        if (rows.Count == 0)
        {
            return;
        }

        var columns = rows.Max(r => r.Length);
        var widths = new int[columns];
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        sb.Append("<pre>");
        foreach (var row in rows)
        {
            var line = string.Join("  ", row.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd();
            sb.Append(Escape(line));
            sb.Append('\n');
        }

        sb.Append("</pre>\n\n");
    }

    private static void RenderInlines(ContainerInline? container, StringBuilder sb)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            RenderInline(inline, sb);
        }
    }

    private static void RenderInline(Inline inline, StringBuilder sb)
    {
        switch (inline)
        {
            case LiteralInline literal:
                sb.Append(Escape(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis:
                var tag = emphasis.DelimiterCount >= 2 ? "b" : "i";
                sb.Append($"<{tag}>");
                RenderInlines(emphasis, sb);
                sb.Append($"</{tag}>");
                break;

            case CodeInline code:
                sb.Append("<code>").Append(Escape(code.Content)).Append("</code>");
                break;

            case LinkInline { IsImage: true } image:
                RenderInlines(image, sb); // само надписа; Telegram не вгражда картинки в текст
                break;

            case LinkInline link:
                sb.Append($"<a href=\"{Escape(link.Url ?? "")}\">");
                RenderInlines(link, sb);
                sb.Append("</a>");
                break;

            case AutolinkInline autolink:
                sb.Append($"<a href=\"{Escape(autolink.Url)}\">{Escape(autolink.Url)}</a>");
                break;

            case LineBreakInline:
                sb.Append('\n');
                break;

            case HtmlInline html:
                sb.Append(Escape(html.Tag));
                break;

            case ContainerInline container:
                RenderInlines(container, sb);
                break;
        }
    }

    /// <summary>Само текстът, без тагове — за клетки на таблици (те са в pre).</summary>
    private static void RenderPlainInlines(ContainerInline container, StringBuilder sb)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ContainerInline nested:
                    RenderPlainInlines(nested, sb);
                    break;
            }
        }
    }
}
