using SearchQuality.Corpus;
using SearchQuality.Domain;

namespace SearchQuality.Tests.Corpus;

public sealed class ApproximateTokenChunkerTests
{
    [Fact]
    public void Chunk_creates_stable_overlapping_chunks()
    {
        var document = new SourceDocument(
            "policy",
            "Policy",
            "policy.md",
            string.Join(' ', Enumerable.Range(1, 25).Select(value => $"w{value}")));
        var chunker = new ApproximateTokenChunker(10, 2);

        var chunks = chunker.Chunk(document);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("policy::0000", chunks[0].Id);
        Assert.Contains("w9", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains("w9", chunks[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("w25", chunks[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_overlap_equal_to_chunk_size()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ApproximateTokenChunker(10, 10));
        Assert.Contains("overlap", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
