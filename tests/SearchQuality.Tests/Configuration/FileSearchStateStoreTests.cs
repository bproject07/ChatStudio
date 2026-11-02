using SearchQuality.Configuration;
using SearchQuality.OpenAI;

namespace SearchQuality.Tests.Configuration;

public sealed class FileSearchStateStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsStateAtomically()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "state", "file-search-index.json");
            var expected = new FileSearchIndexState(
                "vs_test",
                ["file_1", "file_2"],
                new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));

            await FileSearchStateStore.SaveAsync(path, expected, TestContext.Current.CancellationToken);
            var actual = await FileSearchStateStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.NotNull(actual);
            Assert.Equal(expected.VectorStoreId, actual.VectorStoreId);
            Assert.Equal(expected.FileIds, actual.FileIds);
            Assert.Equal(expected.CreatedAt, actual.CreatedAt);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SearchQuality.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
