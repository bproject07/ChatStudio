using SearchQuality.Chat;

namespace SearchQuality.Chat.Tests;

public sealed class ChatArtifactMarkdownTests
{
    private static readonly DateTimeOffset Stamp =
        new(2026, 7, 25, 14, 5, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Existing_h1_is_kept_once_and_metadata_is_inserted_after_it()
    {
        var markdown = ChatService.BuildArtifactMarkdown(
            "maria",
            "рецепта",
            "Къри с ориз",
            "# Къри с ориз\n\n## Съставки\n- ориз — 200 г",
            Stamp);

        Assert.Equal(
            """
            # Къри с ориз

            Потребител: maria · Вид: рецепта · Записано: 25.07.2026 14:05

            ## Съставки
            - ориз — 200 г
            """,
            markdown);
    }

    [Fact]
    public void Missing_h1_gets_the_document_title()
    {
        var markdown = ChatService.BuildArtifactMarkdown(
            "maria",
            "режим",
            "Седмично меню",
            "## Понеделник\n- супа",
            Stamp);

        Assert.Equal(
            """
            # Седмично меню

            Потребител: maria · Вид: режим · Записано: 25.07.2026 14:05

            ## Понеделник
            - супа
            """,
            markdown);
    }

    [Fact]
    public void Full_replacement_refreshes_existing_metadata_instead_of_duplicating_it()
    {
        var markdown = ChatService.BuildReplacementArtifactMarkdown(
            "ivan",
            "програма",
            "Програма",
            """
            # Програма

            Потребител: ivan · Вид: програма · Записано: 01.08.2026 10:00

            ## Ден 1

            Клек
            """,
            new DateTimeOffset(2026, 8, 2, 12, 30, 0, TimeSpan.FromHours(3)));

        Assert.Equal(1, CountOccurrences(markdown, "Потребител:"));
        Assert.Contains("Записано: 02.08.2026 12:30", markdown);
        Assert.Contains("## Ден 1", markdown);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
