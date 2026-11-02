namespace SearchQuality.Chat;

/// <summary>
/// Началните промптове — влизат в базата като v1 при първото стартиране (seed)
/// и НЕ се четат по време на работа: истината е в prompt_versions.
/// </summary>
public static class DefaultPrompts
{
    public const string Chat = ChatPromptPolicy.ChatBase;

    /// <summary>
    /// Каноничният production prompt за фитнес специалист. Съществуващите
    /// агенти получават нова версия през версионирания prompt API.
    /// </summary>
    public const string Coach = ChatPromptPolicy.CoachBase;

    public const string Compactor =
        """
        Резюмирай следния разговор на български в стегнат markdown.
        Запази: конкретните факти, числа и дати; решенията и предпочитанията на
        потребителя; отворените въпроси. Пропусни поздравите и повторенията.
        Пиши в трето лице (какво е споделил потребителят, какво е отговорено).
        """;
}
