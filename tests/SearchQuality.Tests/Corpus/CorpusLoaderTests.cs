using SearchQuality.Corpus;

namespace SearchQuality.Tests.Corpus;

public sealed class CorpusLoaderTests
{
    [Fact]
    public async Task LoadDocuments_AllowsAnEmptyDirectoryWhenRequested()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"search-quality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var documents = await CorpusLoader.LoadDocumentsAsync(
                directory,
                TestContext.Current.CancellationToken,
                allowEmpty: true);

            Assert.Empty(documents);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadDocuments_uses_filename_as_id_and_heading_as_title()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"search-quality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "leave.md"),
                "# Leave policy\n\nBody",
                TestContext.Current.CancellationToken);

            var documents = await CorpusLoader.LoadDocumentsAsync(
                directory,
                TestContext.Current.CancellationToken);

            var document = Assert.Single(documents);
            Assert.Equal("leave", document.Id);
            Assert.Equal("Leave policy", document.Title);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
