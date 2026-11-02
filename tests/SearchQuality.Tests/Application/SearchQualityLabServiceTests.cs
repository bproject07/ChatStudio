using SearchQuality.Application;
using SearchQuality.Configuration;
using SearchQuality.OpenAI;
using SearchQuality.Postgres;

namespace SearchQuality.Tests.Application;

public sealed class SearchQualityLabServiceTests
{
    [Fact]
    public async Task SearchAsync_RejectsUnknownStrategyBeforeExternalCalls()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
            "тестов въпрос",
            5,
            ["not-a-strategy"],
            TestContext.Current.CancellationToken));

        Assert.Contains("not-a-strategy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_AcceptsStrategyNamesCaseInsensitively()
    {
        // Уникален корен гарантира, че няма File Search state файл и
        // стратегията завършва бързо с UnavailablePipeline без мрежови заявки.
        var root = Directory.CreateTempSubdirectory("sq-lab-tests").FullName;
        try
        {
            var service = CreateService(root);

            var result = await service.SearchAsync(
                "тестов въпрос",
                5,
                ["OPENAI-File-Search"],
                TestContext.Current.CancellationToken);

            var single = Assert.Single(result.Results);
            Assert.Equal(SearchStrategyNames.FileSearch, single.Strategy);
            Assert.NotNull(single.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_RejectsUnboundedTopK()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync(
            "тестов въпрос",
            21,
            [SearchStrategyNames.FullText],
            TestContext.Current.CancellationToken));
    }

    private static SearchQualityLabService CreateService(string? rootDirectory = null)
    {
        var openAiSettings = new OpenAiSettings(
            "test-key",
            "https://api.openai.com/v1",
            "test-generator",
            "low",
            "test-judge",
            "test-embedding",
            3,
            100,
            800,
            400);
        var settings = new LabSettings(
            rootDirectory ?? Path.GetTempPath(),
            openAiSettings,
            new PostgresSettings("Host=unused", "search_quality_lab"),
            new BenchmarkSettings(5, 450, 90),
            "test",
            "test");
        return new SearchQualityLabService(
            settings,
            new OpenAiApiClient(new HttpClient(), openAiSettings));
    }
}
