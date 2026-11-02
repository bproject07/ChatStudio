using SearchQuality.Configuration;

namespace SearchQuality.Tests.Configuration;

public sealed class LabSettingsLoaderTests
{
    [Fact]
    public void Local_appsettings_overlays_string_and_numeric_values()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sq-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.json"),
                """
                {
                  "OpenAI": {
                    "BaseUrl": "https://api.openai.com/v1",
                    "GeneratorModel": "gpt-5.4-mini",
                    "GeneratorReasoningEffort": "medium",
                    "JudgeModel": "gpt-5.4-mini",
                    "EmbeddingModel": "text-embedding-3-large",
                    "EmbeddingDimensions": 1536,
                    "MaxOutputTokens": 700
                  },
                  "Postgres": {
                    "Schema": "search_quality_lab"
                  },
                  "Benchmark": {
                    "TopK": 5,
                    "ChunkSizeWords": 450,
                    "ChunkOverlapWords": 90,
                    "FileSearchChunkSizeTokens": 800,
                    "FileSearchChunkOverlapTokens": 400
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.Local.json"),
                """
                {
                  "OpenAI": {
                    "GeneratorModel": "custom-generator",
                    "EmbeddingModel": "custom-embedding",
                    "EmbeddingDimensions": 512,
                    "MaxOutputTokens": 1200
                  },
                  "Postgres": {
                    "Schema": "custom_schema"
                  },
                  "Benchmark": {
                    "TopK": 8,
                    "ChunkSizeWords": 300,
                    "ChunkOverlapWords": 60,
                    "FileSearchChunkSizeTokens": 600,
                    "FileSearchChunkOverlapTokens": 200
                  }
                }
                """);

            var settings = LabSettingsLoader.Load(tempRoot);

            Assert.Equal("custom-generator", settings.OpenAI.GeneratorModel);
            Assert.Equal("custom-embedding", settings.OpenAI.EmbeddingModel);
            Assert.Equal(512, settings.OpenAI.EmbeddingDimensions);
            Assert.Equal(1200, settings.OpenAI.MaxOutputTokens);
            Assert.Equal(600, settings.OpenAI.FileSearchChunkSizeTokens);
            Assert.Equal(200, settings.OpenAI.FileSearchChunkOverlapTokens);
            Assert.Equal("custom_schema", settings.Postgres.Schema);
            Assert.Equal(8, settings.Benchmark.TopK);
            Assert.Equal(300, settings.Benchmark.ChunkSizeWords);
            Assert.Equal(60, settings.Benchmark.ChunkOverlapWords);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Required_string_settings_can_be_supplied_solely_in_local_appsettings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sq-settings-local-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.json"),
                """
                {
                  "OpenAI": {
                    "BaseUrl": "https://api.openai.com/v1",
                    "EmbeddingDimensions": 1536,
                    "MaxOutputTokens": 700
                  },
                  "Postgres": {},
                  "Benchmark": {
                    "TopK": 5,
                    "ChunkSizeWords": 450,
                    "ChunkOverlapWords": 90,
                    "FileSearchChunkSizeTokens": 800,
                    "FileSearchChunkOverlapTokens": 400
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.Local.json"),
                """
                {
                  "OpenAI": {
                    "GeneratorModel": "local-only-generator",
                    "JudgeModel": "local-only-judge",
                    "EmbeddingModel": "local-only-embedding"
                  },
                  "Postgres": {
                    "Schema": "local_only_schema"
                  }
                }
                """);

            var settings = LabSettingsLoader.Load(tempRoot);

            Assert.Equal("local-only-generator", settings.OpenAI.GeneratorModel);
            Assert.Equal("local-only-judge", settings.OpenAI.JudgeModel);
            Assert.Equal("local-only-embedding", settings.OpenAI.EmbeddingModel);
            Assert.Equal("local_only_schema", settings.Postgres.Schema);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Malformed_local_appsettings_throws_InvalidOperationException()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sq-settings-bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.json"),
                """
                {
                  "OpenAI": {
                    "BaseUrl": "https://api.openai.com/v1",
                    "GeneratorModel": "gpt-5.4-mini",
                    "JudgeModel": "gpt-5.4-mini",
                    "EmbeddingModel": "text-embedding-3-large",
                    "EmbeddingDimensions": 1536,
                    "MaxOutputTokens": 700
                  },
                  "Postgres": { "Schema": "search_quality_lab" },
                  "Benchmark": {
                    "TopK": 5,
                    "ChunkSizeWords": 450,
                    "ChunkOverlapWords": 90,
                    "FileSearchChunkSizeTokens": 800,
                    "FileSearchChunkOverlapTokens": 400
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.Local.json"),
                "{ invalid json ");

            var exception = Assert.Throws<InvalidOperationException>(() => LabSettingsLoader.Load(tempRoot));
            Assert.Contains("appsettings.Local.json", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
