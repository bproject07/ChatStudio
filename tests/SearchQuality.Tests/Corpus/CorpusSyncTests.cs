using SearchQuality.Corpus;
using SearchQuality.Domain;

namespace SearchQuality.Tests.Corpus;

public sealed class CorpusSyncTests
{
    private const string Signature = "pg|450|90|text-embedding-3-large|1536";

    private static SourceDocument Document(string id, string content = "съдържание") =>
        new(id, $"Заглавие {id}", $"{id}.md", content);

    [Fact]
    public void ComputeDocumentHash_IsStableForSameInput()
    {
        var first = CorpusSync.ComputeDocumentHash(Document("a"), Signature);
        var second = CorpusSync.ComputeDocumentHash(Document("a"), Signature);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("друго съдържание")]
    [InlineData("съдържание ")]
    public void ComputeDocumentHash_ChangesWithContent(string content)
    {
        var baseline = CorpusSync.ComputeDocumentHash(Document("a"), Signature);

        Assert.NotEqual(baseline, CorpusSync.ComputeDocumentHash(Document("a", content), Signature));
    }

    [Fact]
    public void ComputeDocumentHash_ChangesWithIndexingSignature()
    {
        var document = Document("a");

        Assert.NotEqual(
            CorpusSync.ComputeDocumentHash(document, Signature),
            CorpusSync.ComputeDocumentHash(document, "pg|300|60|text-embedding-3-large|1536"));
    }

    [Fact]
    public void ComputeDocumentHash_FieldBoundariesDoNotCollide()
    {
        // Дължинният префикс пази срещу колизии от преместено съдържание
        // между съседни полета (напр. край на заглавие срещу начало на файл).
        var first = new SourceDocument("a", "заглавие", "x.md", "текст");
        var second = new SourceDocument("a", "заглавиеx", ".md", "текст");

        Assert.NotEqual(
            CorpusSync.ComputeDocumentHash(first, Signature),
            CorpusSync.ComputeDocumentHash(second, Signature));
    }

    [Fact]
    public void Compute_ClassifiesAddedChangedRemovedUnchanged()
    {
        var unchanged = Document("unchanged");
        var changed = Document("changed", "нова версия");
        var added = Document("added");
        var indexed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["unchanged"] = CorpusSync.ComputeDocumentHash(unchanged, Signature),
            ["changed"] = CorpusSync.ComputeDocumentHash(Document("changed", "стара версия"), Signature),
            ["removed"] = "whatever"
        };

        var diff = CorpusSync.Compute([unchanged, changed, added], indexed, Signature);

        Assert.Equal(["added"], diff.Added.Select(document => document.Id));
        Assert.Equal(["changed"], diff.Changed.Select(document => document.Id));
        Assert.Equal(["removed"], diff.RemovedIds);
        Assert.Equal(1, diff.UnchangedCount);
        Assert.False(diff.IsEmpty);
        Assert.Equal(["added", "changed"], diff.ToIndex.Select(document => document.Id));
    }

    [Fact]
    public void Compute_IdenticalCorpusIsEmpty()
    {
        var documents = new[] { Document("a"), Document("b") };
        var indexed = documents.ToDictionary(
            document => document.Id,
            document => CorpusSync.ComputeDocumentHash(document, Signature),
            StringComparer.Ordinal);

        var diff = CorpusSync.Compute(documents, indexed, Signature);

        Assert.True(diff.IsEmpty);
        Assert.Equal(2, diff.UnchangedCount);
    }

    [Fact]
    public void Compute_SignatureChangeMarksEverythingChanged()
    {
        var documents = new[] { Document("a"), Document("b") };
        var indexed = documents.ToDictionary(
            document => document.Id,
            document => CorpusSync.ComputeDocumentHash(document, Signature),
            StringComparer.Ordinal);

        var diff = CorpusSync.Compute(documents, indexed, "pg|300|60|text-embedding-3-large|1536");

        Assert.Equal(2, diff.Changed.Count);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.RemovedIds);
    }

    [Fact]
    public void Compute_EmptyCorpusRemovesEverything()
    {
        var diff = CorpusSync.Compute(
            [],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "h1", ["b"] = "h2" },
            Signature);

        Assert.Equal(["a", "b"], diff.RemovedIds);
        Assert.Empty(diff.ToIndex);
        Assert.False(diff.IsEmpty);
    }
}
