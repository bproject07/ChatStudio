using SearchQuality.Chat;
using SearchQuality.Chat.Telegram;

namespace SearchQuality.Chat.Tests;

public sealed class TelegramMarkdownTests
{
    [Fact]
    public void Basic_formatting_maps_to_telegram_tags()
    {
        var html = TelegramMarkdown.ToHtml(
            "## Програма\n\nДен 1: **клекове** и *планк* с `30 сек` почивка.");

        Assert.Contains("<b>Програма</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>клекове</b>", html, StringComparison.Ordinal);
        Assert.Contains("<i>планк</i>", html, StringComparison.Ordinal);
        Assert.Contains("<code>30 сек</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("##", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_tables_and_code_render_without_unsupported_tags()
    {
        var html = TelegramMarkdown.ToHtml(
            """
            - първо
            - второ
              1. вложено

            | Ден | Тренировка |
            |-----|------------|
            | Пн  | A          |

            ```
            var x = 1 < 2;
            ```
            """);

        Assert.Contains("• първо", html, StringComparison.Ordinal);
        Assert.Contains("1. вложено", html, StringComparison.Ordinal);
        Assert.Contains("<pre>", html, StringComparison.Ordinal);
        Assert.Contains("Пн", html, StringComparison.Ordinal);
        Assert.Contains("1 &lt; 2", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<ul>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<table>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Special_characters_are_escaped_and_links_kept()
    {
        var html = TelegramMarkdown.ToHtml(
            "Сравни a<b с [сайта](https://example.com/x?a=1&b=2).");

        Assert.Contains("a&lt;b", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com/x?a=1&amp;b=2\">сайта</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_text_is_split_into_chunks_on_paragraph_boundaries()
    {
        var paragraph = "Изречение с достатъчно текст за тежест. ";
        var markdown = string.Join(
            "\n\n",
            Enumerable.Range(1, 40).Select(i => $"**Абзац {i}.** {string.Concat(Enumerable.Repeat(paragraph, 5))}"));

        var chunks = TelegramMarkdown.ToChunks(markdown);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 4096, $"парче от {chunk.Length} знака"));
        Assert.Contains("<b>Абзац 1.</b>", chunks[0], StringComparison.Ordinal);
        Assert.Contains($"<b>Абзац 40.</b>", chunks[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Escaping_expansion_still_stays_below_telegram_limit()
    {
        var chunks = TelegramMarkdown.ToChunks(new string('&', 5000));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(
            chunk.Length <= 3900,
            $"парче от {chunk.Length} знака"));
        Assert.Equal(5000, chunks.Sum(chunk =>
            chunk.Split("&amp;", StringSplitOptions.None).Length - 1));
    }

    [Fact]
    public void Diary_confirmation_is_structured_and_escaped()
    {
        var html = TelegramMarkdown.FormatDiaryConfirmations(
        [
            new DiaryConfirmation(
                DiaryConfirmationStatus.Recorded,
                "упражнение",
                "Cable <row>",
                42,
                "Day 2",
                """{"серии":4,"повторения":12,"тегло_кг":30}""",
                "1x15 & 3x12",
                null)
        ]);

        Assert.Contains("<b>Дневник</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Записано:</b> Cable &lt;row&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Day 2 · упражнение · id 42", html, StringComparison.Ordinal);
        Assert.Contains("4 серии · 12 повторения · 30 кг", html, StringComparison.Ordinal);
        Assert.Contains("1x15 &amp; 3x12", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejected_diary_operation_says_nothing_was_recorded()
    {
        var html = TelegramMarkdown.FormatDiaryConfirmations(
        [
            new DiaryConfirmation(
                DiaryConfirmationStatus.Rejected,
                "дневник",
                "Пакет",
                Message: "Дубликат.")
        ]);

        Assert.Contains("Нищо не е записано", html, StringComparison.Ordinal);
        Assert.Contains("Дубликат.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Entity_heavy_confirmation_stays_within_telegram_limit()
    {
        // Escape-ването умножава дължината (& → &amp;): преди твърдия cap
        // това потвърждение рендерираше ~7600 знака и Telegram го отказваше.
        var confirmation = new DiaryConfirmation(
            DiaryConfirmationStatus.Recorded,
            "упражнение",
            new string('&', 480),
            123456,
            "Day 2 — Upper + Core " + new string('&', 120),
            """{"серии":4,"повторения":12,"тегло_кг":42}""",
            new string('&', 340),
            new string('&', 480));

        var chunks = TelegramMarkdown.ToDiaryConfirmationChunks([confirmation]);

        Assert.Single(chunks);
        Assert.True(chunks[0].Length <= 3900);
        Assert.EndsWith("…", chunks[0], StringComparison.Ordinal);
        // Срязването не оставя започнато entity — HTML-ът остава валиден.
        var lastAmpersand = chunks[0].LastIndexOf('&');
        Assert.True(
            lastAmpersand < 0
            || chunks[0].IndexOf(';', lastAmpersand) > lastAmpersand);
    }

    [Fact]
    public void Many_confirmations_split_into_chunks_under_the_limit()
    {
        var confirmations = Enumerable.Range(1, 40)
            .Select(index => new DiaryConfirmation(
                DiaryConfirmationStatus.Recorded,
                "упражнение",
                $"Упражнение с дълго име и уточнения номер {index} " + new string('x', 180),
                index,
                "Day 2 — Upper + Core",
                """{"серии":4,"повторения":12,"тегло_кг":42}""",
                "3x12x40, 3x12x42, 3x12x44"))
            .ToArray();

        var chunks = TelegramMarkdown.ToDiaryConfirmationChunks(confirmations);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 3900));
        Assert.All(chunks, chunk =>
            Assert.StartsWith("<b>Дневник</b>", chunk, StringComparison.Ordinal));
    }
}
