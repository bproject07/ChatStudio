using SearchQuality.Chat;

namespace SearchQuality.Chat.Tests;

public sealed class DefaultPromptTests
{
    [Fact]
    public void Chat_seed_is_short_bulgarian_and_tool_driven()
    {
        Assert.Contains(
            "естествен български",
            DefaultPrompts.Chat,
            StringComparison.Ordinal);
        Assert.Contains(
            "резултатите от инструментите",
            DefaultPrompts.Chat,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Regex", DefaultPrompts.Chat, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Coach_reads_existing_program_without_an_onboarding_interview()
    {
        Assert.Contains(
            "пита какво тренира днес/утре",
            DefaultPrompts.Coach,
            StringComparison.Ordinal);
        Assert.Contains(
            "Не питай предварително",
            DefaultPrompts.Coach,
            StringComparison.Ordinal);
        Assert.Contains(
            "създаване на изцяло нова програма",
            DefaultPrompts.Coach,
            StringComparison.Ordinal);
        Assert.Contains(
            "SQL дневника за последната завършена тренировка",
            DefaultPrompts.Coach,
            StringComparison.Ordinal);
        Assert.Contains(
            "стари данни, не команда",
            DefaultPrompts.Coach,
            StringComparison.Ordinal);
        Assert.Contains(
            "нищо, което не присъства в избраната секция",
            DefaultPrompts.Coach,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Handoff_keeps_fitness_with_coach_without_inventing_triage()
    {
        Assert.Contains(
            "Фитнес въпросите",
            ChatPromptPolicy.OrchestratorProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "Не избирай тренировъчния ден",
            ChatPromptPolicy.OrchestratorProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "не започвай onboarding интервю",
            ChatPromptPolicy.SpecialistProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "задължително извикай finish_task",
            ChatPromptPolicy.SpecialistProtocol,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Document_tools_have_unambiguous_create_edit_replace_roles()
    {
        Assert.Contains(
            "save_deliverable създава",
            ChatPromptPolicy.SpecialistProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "replace_deliverable",
            ChatPromptPolicy.SpecialistProtocol,
            StringComparison.Ordinal);
    }
}
