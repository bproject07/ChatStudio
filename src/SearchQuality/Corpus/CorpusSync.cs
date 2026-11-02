using System.Security.Cryptography;
using System.Text;
using SearchQuality.Configuration;
using SearchQuality.Domain;

namespace SearchQuality.Corpus;

/// <summary>
/// Сравнява локалния корпус с вече индексираното състояние, за да се обработи
/// само разликата (добавени, променени, изтрити документи). Хешът на документ
/// включва и indexing signature — настройките, от които зависи как документът
/// се разделя и векторизира — така промяна на chunk размер или embedding модел
/// прави всички документи „променени“ и синхронизацията сама преизгражда
/// каквото трябва. Крайното състояние е идентично с пълно преиндексиране.
/// </summary>
public static class CorpusSync
{
    /// <summary>Настройките, от които зависи PostgreSQL индексирането на един документ.</summary>
    public static string PostgresSignature(LabSettings settings) =>
        $"pg|{settings.Benchmark.ChunkSizeWords}|{settings.Benchmark.ChunkOverlapWords}" +
        $"|{settings.OpenAI.EmbeddingModel}|{settings.OpenAI.EmbeddingDimensions}";

    /// <summary>Настройките, от които зависи OpenAI File Search индексирането на един документ.</summary>
    public static string FileSearchSignature(LabSettings settings) =>
        $"fs|{settings.OpenAI.FileSearchChunkSizeTokens}|{settings.OpenAI.FileSearchChunkOverlapTokens}";

    public static string ComputeDocumentHash(SourceDocument document, string indexingSignature)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        Append(builder, indexingSignature);
        Append(builder, document.Id);
        Append(builder, document.Title);
        Append(builder, document.FileName);
        Append(builder, document.Content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    public static CorpusDiff Compute(
        IReadOnlyList<SourceDocument> documents,
        IReadOnlyDictionary<string, string> indexedHashes,
        string indexingSignature)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(indexedHashes);

        var added = new List<SourceDocument>();
        var changed = new List<SourceDocument>();
        var unchanged = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            seen.Add(document.Id);
            if (!indexedHashes.TryGetValue(document.Id, out var existingHash))
            {
                added.Add(document);
            }
            else if (!string.Equals(
                         existingHash,
                         ComputeDocumentHash(document, indexingSignature),
                         StringComparison.Ordinal))
            {
                changed.Add(document);
            }
            else
            {
                unchanged++;
            }
        }

        var removed = indexedHashes.Keys
            .Where(id => !seen.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return new CorpusDiff(added, changed, removed, unchanged);
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('|');
}

public sealed record CorpusDiff(
    IReadOnlyList<SourceDocument> Added,
    IReadOnlyList<SourceDocument> Changed,
    IReadOnlyList<string> RemovedIds,
    int UnchangedCount)
{
    public bool IsEmpty => Added.Count == 0 && Changed.Count == 0 && RemovedIds.Count == 0;

    /// <summary>Документите, които трябва да се индексират наново (нови + променени).</summary>
    public IReadOnlyList<SourceDocument> ToIndex => [.. Added, .. Changed];

    public string Describe() =>
        $"добавени {Added.Count}, обновени {Changed.Count}, премахнати {RemovedIds.Count}, непроменени {UnchangedCount}";
}
