using System.Text.RegularExpressions;
using SearchQuality.Abstractions;
using SearchQuality.Domain;

namespace SearchQuality.Corpus;

public sealed partial class ApproximateTokenChunker : IDocumentChunker
{
    private readonly int _chunkSizeWords;
    private readonly int _overlapWords;

    public ApproximateTokenChunker(int chunkSizeWords, int overlapWords)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSizeWords, 10);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapWords);

        if (overlapWords >= chunkSizeWords)
        {
            throw new ArgumentException("Chunk overlap must be smaller than the chunk size.", nameof(overlapWords));
        }

        _chunkSizeWords = chunkSizeWords;
        _overlapWords = overlapWords;
    }

    public IReadOnlyList<DocumentChunk> Chunk(SourceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var words = NonWhitespaceRegex()
            .Matches(document.Content)
            .Select(match => match.Value)
            .ToArray();

        if (words.Length == 0)
        {
            return [];
        }

        var chunks = new List<DocumentChunk>();
        var step = _chunkSizeWords - _overlapWords;

        for (var start = 0; start < words.Length; start += step)
        {
            var length = Math.Min(_chunkSizeWords, words.Length - start);
            var ordinal = chunks.Count;
            var text = string.Join(' ', words.AsSpan(start, length).ToArray());

            chunks.Add(new DocumentChunk(
                $"{document.Id}::{ordinal:D4}",
                document.Id,
                document.Title,
                ordinal,
                text));

            if (start + length >= words.Length)
            {
                break;
            }
        }

        return chunks;
    }

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWhitespaceRegex();
}
