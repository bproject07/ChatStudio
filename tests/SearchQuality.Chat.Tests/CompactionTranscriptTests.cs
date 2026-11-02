using SearchQuality.Chat;

namespace SearchQuality.Chat.Tests;

public sealed class CompactionTranscriptTests
{
    [Fact]
    public void Transcript_is_simple_chronological_dialogue_without_system_notes()
    {
        var at = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        ChatMessage[] messages =
        [
            new("user", "4х12х50кг готово", at),
            new(
                "assistant",
                "Записах 4х12х50кг.",
                at.AddSeconds(1),
                Steps: ["🏋 дневник: 4х12х50кг"]),
            new("system", "— Предадено на Треньор —", at.AddSeconds(2)),
            new(
                "assistant",
                "Това е общ съвет, не потвърждение за извършено действие.",
                at.AddSeconds(3)),
            new("user", "Покажи дневника ми за днес.", at.AddSeconds(4)),
            new("assistant", "Стар отговор без tool стъпка.", at.AddSeconds(5)),
            new("user", "Предишният запис беше грешен.", at.AddSeconds(6))
        ];

        var transcript = ChatService.BuildCompactionTranscript(messages);

        Assert.Contains("Предишният запис беше грешен.", transcript);
        Assert.Contains("4х12х50кг готово", transcript);
        Assert.Contains("Покажи дневника", transcript);
        Assert.Contains("Записах 4х12х50кг.", transcript);
        Assert.Contains("Асистент:", transcript);
        Assert.DoesNotContain("Предадено на Треньор", transcript);
        Assert.Contains("Това е общ съвет", transcript);
    }

    [Fact]
    public void Summary_normalization_only_handles_empty_output()
    {
        const string summary =
            """
            - Потвърдено:
              - Предишните грешни записи не са валидни.

            - Отворени въпроси/действия за асистента:
              - Да покаже дневника.

            - Предпочитания:
              - Отговорите да са кратки.
            """;

        var normalized = ChatService.NormalizeCompactionSummary(summary);

        Assert.Contains("Потвърдено", normalized);
        Assert.Contains("Предпочитания", normalized);
        Assert.Contains("Отворени", normalized);
        Assert.Contains("Да покаже дневника", normalized);
        Assert.Equal(
            "Няма нови дългосрочни факти за запазване.",
            ChatService.NormalizeCompactionSummary("  "));
    }
}
