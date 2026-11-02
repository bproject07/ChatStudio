using System.Text.Json;
using SearchQuality.Domain;

namespace SearchQuality.Corpus;

public static class CorpusLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<IReadOnlyList<SourceDocument>> LoadDocumentsAsync(
        string directory,
        CancellationToken cancellationToken = default,
        bool allowEmpty = false)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Document directory was not found: {directory}");
        }

        var documents = new List<SourceDocument>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                     .Where(DocumentTextExtractor.IsSupported)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var extracted = await DocumentTextExtractor.ExtractAsync(path, cancellationToken);
            var id = Path.GetFileNameWithoutExtension(path);
            if (documents.Any(document => string.Equals(
                    document.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Повече от един документ използва идентификатора „{id}“. Преименувайте единия файл.");
            }

            documents.Add(new SourceDocument(
                id,
                extracted.Title,
                Path.GetFileName(path),
                extracted.Content));
        }

        if (!allowEmpty && documents.Count == 0)
        {
            throw new InvalidOperationException(
                $"Не бяха намерени поддържани документи в {directory}. " +
                $"Разрешени формати: {string.Join(", ", DocumentTextExtractor.SupportedExtensions)}.");
        }

        return documents;
    }

    public static async Task<IReadOnlyList<EvaluationCase>> LoadEvaluationCasesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var cases = await JsonSerializer.DeserializeAsync<List<EvaluationCase>>(
            stream,
            JsonOptions,
            cancellationToken);

        if (cases is null || cases.Count == 0)
        {
            throw new InvalidOperationException($"No evaluation cases were found in {path}.");
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (evaluation, index) in cases.Select((item, index) => (item, index)))
        {
            // Конструкторната десериализация оставя липсващи JSON полета null,
            // затова всеки случай се проверява преди да стигне до метриките.
            if (string.IsNullOrWhiteSpace(evaluation.Id))
            {
                throw new InvalidOperationException(
                    $"Evaluation case at position {index + 1} in {path} has no id.");
            }

            if (!seenIds.Add(evaluation.Id))
            {
                throw new InvalidOperationException(
                    $"Evaluation case id '{evaluation.Id}' is duplicated in {path}.");
            }

            if (string.IsNullOrWhiteSpace(evaluation.Question))
            {
                throw new InvalidOperationException(
                    $"Evaluation case '{evaluation.Id}' has no question.");
            }

            if (evaluation.ExpectedDocumentIds is null
                || evaluation.ExpectedDocumentIds.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"Evaluation case '{evaluation.Id}' has a missing or empty expectedDocumentIds entry.");
            }

            if (evaluation.RequiredTerms is null
                || evaluation.RequiredTerms.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"Evaluation case '{evaluation.Id}' has a missing or empty requiredTerms entry.");
            }
        }

        return cases;
    }
}
