namespace SearchQuality.Chat.Tests;

public sealed class ChatSettingsLoaderTests
{
    [Fact]
    public void Local_appsettings_overlays_chat_settings_and_users()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"chat-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.json"),
                """
                {
                  "Chat": {
                    "ContextTokenBudget": 64000,
                    "TopK": 4,
                    "DefaultStrategy": "postgres-hybrid",
                    "Schema": "chat_lab",
                    "Model": "gpt-5.4-mini",
                    "Provider": "litellm",
                    "MaxOutputTokens": 15000,
                    "Users": [
                      { "Id": "ivan", "Name": "Иван" },
                      { "Id": "maria", "Name": "Мария" }
                    ]
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.Local.json"),
                """
                {
                  "Chat": {
                    "Model": "custom-chat-model",
                    "Provider": "openai",
                    "MaxOutputTokens": 8000,
                    "Users": [
                      { "Id": "alex", "Name": "Alex" }
                    ]
                  }
                }
                """);

            var settings = ChatSettingsLoader.Load(tempRoot);

            Assert.Equal("custom-chat-model", settings.Model);
            Assert.Equal("openai", settings.Provider);
            Assert.Equal(8000, settings.MaxOutputTokens);
            Assert.Equal(64000, settings.ContextTokenBudget);
            var user = Assert.Single(settings.Users);
            Assert.Equal("alex", user.Id);
            Assert.Equal("Alex", user.Name);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadLiteLlm_merges_fields_across_local_and_external_configuration()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"chat-litellm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var externalPath = Path.Combine(tempRoot, "external.json");
            File.WriteAllText(
                externalPath,
                """
                {
                  "LiteLLM": {
                    "BaseUrl": "http://external-host:4000/v1",
                    "ApiKey": "external-key-value"
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.json"),
                "{}");

            File.WriteAllText(
                Path.Combine(tempRoot, "appsettings.Local.json"),
                $$"""
                {
                  "ExternalConfiguration": {
                    "FitnessAppSettings": {{System.Text.Json.JsonSerializer.Serialize(externalPath)}}
                  },
                  "LiteLLM": {
                    "BaseUrl": "http://local-override:4000/v1"
                  }
                }
                """);

            var result = ChatSettingsLoader.LoadLiteLlm(tempRoot);

            Assert.NotNull(result);
            Assert.Equal("http://local-override:4000/v1", result.Value.BaseUrl);
            Assert.Equal("external-key-value", result.Value.ApiKey);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Malformed_local_appsettings_throws_on_Load_and_LoadLiteLlm()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"chat-bad-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "appsettings.json"), "{}");
            File.WriteAllText(Path.Combine(tempRoot, "appsettings.Local.json"), "{ broken json");

            Assert.Throws<InvalidOperationException>(() => ChatSettingsLoader.Load(tempRoot));
            Assert.Throws<InvalidOperationException>(() => ChatSettingsLoader.LoadLiteLlm(tempRoot));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
