namespace SearchQuality.Benchmarking;

public static class BenchmarkProtocol
{
    /// <summary>
    /// Версия на експерименталния протокол. Участва в конфигурационния
    /// fingerprint и покрива всичко, което влияе на резултатите, но не се чете
    /// от настройките. Увеличава се при промяна на:
    /// - prompt инструкциите към генератора, Judge модела или File Search;
    /// - retrieval алгоритмите или candidate widening формулата
    ///   (clamp(topK * 4, 20, 100) в PostgresHybridRetriever);
    /// - SQL/tsquery семантиката (LexicalQueryBuilder, PostgresSearchStore);
    /// - начина на генериране или структурата на подавания контекст.
    /// Не се използва за формулите на метриките — те имат собствена версия
    /// (SearchMetrics.CurrentMetricsVersion).
    ///
    /// v2 (2026-07-17): Judge извикването вече праща изричен reasoning effort
    /// (конфигурирания "low") и по-висок max_output_tokens резерв — преди
    /// съдията мислеше на подразбиране и стигаше 'incomplete' преди JSON-а.
    /// Оценките на съдията може да се различават от v1 отчетите.
    ///
    /// v3 (2026-07-18): Генеративният маршрут стана конфигурируем
    /// (OpenAI:GenerationProvider=litellm → Codex през /chat/completions вместо
    /// /responses; изключен по подразбиране — генераторът е на директния
    /// OpenAI). Кой маршрут е активен участва във fingerprint-а
    /// (GenerationBaseUrl/UsesChatCompletions), затова смяната му не иска
    /// нова протоколна версия. Embeddings, File Search и съдията винаги
    /// остават на директния OpenAI маршрут.
    /// </summary>
    public const int Version = 3;
}
