using SearchQuality.Abstractions;
using SearchQuality.Domain;

namespace SearchQuality.OpenAI;

public sealed class OpenAiFileSearchPipeline(
    OpenAiApiClient client,
    string vectorStoreId) : ISearchPipeline
{
    public string Name => "openai-file-search";

    public Task<PipelineResult> ExecuteAsync(
        string question,
        int topK,
        CancellationToken cancellationToken) =>
        client.SearchFilesAndAnswerAsync(vectorStoreId, question, topK, cancellationToken);
}
