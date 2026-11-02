using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SearchQuality.Corpus;

public sealed record ExtractedDocumentText(string Title, string Content);

public static partial class DocumentTextExtractor
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [".md", ".txt", ".pdf", ".docx"];

    public static bool IsSupported(string pathOrFileName)
    {
        var extension = Path.GetExtension(pathOrFileName);
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<ExtractedDocumentText> ExtractAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!IsSupported(extension))
        {
            throw new NotSupportedException(
                $"Форматът „{extension}“ не се поддържа. Разрешени са: {string.Join(", ", SupportedExtensions)}.");
        }

        try
        {
            var extracted = extension switch
            {
                ".md" => await ExtractTextFileAsync(path, isMarkdown: true, cancellationToken),
                ".txt" => await ExtractTextFileAsync(path, isMarkdown: false, cancellationToken),
                ".pdf" => await Task.Run(() => ExtractPdf(path, cancellationToken), cancellationToken),
                ".docx" => await Task.Run(() => ExtractDocx(path, cancellationToken), cancellationToken),
                _ => throw new UnreachableException()
            };

            var content = NormalizeContent(extracted.Content);
            if (string.IsNullOrWhiteSpace(content))
            {
                var detail = extension == ".pdf"
                    ? " PDF файлът може да е сканирано изображение и да изисква OCR."
                    : string.Empty;
                throw new InvalidDataException(
                    $"От „{Path.GetFileName(path)}“ не беше извлечен текст.{detail}");
            }

            var title = string.IsNullOrWhiteSpace(extracted.Title)
                ? Path.GetFileNameWithoutExtension(path)
                : extracted.Title.Trim();
            return new ExtractedDocumentText(title, content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                $"„{Path.GetFileName(path)}“ не може да бъде прочетен като {extension.TrimStart('.').ToUpperInvariant()} документ.",
                exception);
        }
    }

    private static async Task<ExtractedDocumentText> ExtractTextFileAsync(
        string path,
        bool isMarkdown,
        CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, StrictUtf8, cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"„{Path.GetFileName(path)}“ не е валиден UTF-8 текстов файл.",
                exception);
        }

        var firstLine = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var title = isMarkdown && firstLine?.StartsWith("# ", StringComparison.Ordinal) == true
            ? firstLine.TrimStart('#', ' ')
            : firstLine is { Length: <= 160 }
                ? firstLine
                : Path.GetFileNameWithoutExtension(path);
        return new ExtractedDocumentText(title, content);
    }

    private static ExtractedDocumentText ExtractPdf(
        string path,
        CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        var content = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageText = ContentOrderTextExtractor.GetText(page).Trim();
            if (pageText.Length == 0)
            {
                continue;
            }

            if (content.Length > 0)
            {
                content.AppendLine().AppendLine();
            }

            content.Append(pageText);
        }

        return new ExtractedDocumentText(
            document.Information.Title ?? Path.GetFileNameWithoutExtension(path),
            content.ToString());
    }

    private static ExtractedDocumentText ExtractDocx(
        string path,
        CancellationToken cancellationToken)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("Word документът няма основно текстово съдържание.");
        var content = new StringBuilder();
        string? headingTitle = null;

        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = paragraph.InnerText.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            var headingLevel = HeadingLevel(style);
            if (headingLevel == 1 && headingTitle is null)
            {
                headingTitle = text;
            }

            if (content.Length > 0)
            {
                content.AppendLine().AppendLine();
            }

            if (headingLevel > 0)
            {
                content.Append('#', headingLevel).Append(' ');
            }

            content.Append(text);
        }

        var title = FirstNonEmpty(
            document.PackageProperties.Title,
            headingTitle,
            Path.GetFileNameWithoutExtension(path));
        return new ExtractedDocumentText(title!, content.ToString());
    }

    private static int HeadingLevel(string? style)
    {
        if (string.Equals(style, "Title", StringComparison.OrdinalIgnoreCase)
            || string.Equals(style, "Heading1", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var match = HeadingStyleRegex().Match(style ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var level)
            ? Math.Clamp(level, 1, 6)
            : 0;
    }

    private static string NormalizeContent(string content) =>
        ExcessBlankLinesRegex()
            .Replace(UnsupportedControlCharactersRegex().Replace(
                content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
                " "), "\n\n")
            .Trim();

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    [GeneratedRegex("^Heading([1-6])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingStyleRegex();

    [GeneratedRegex("[\\u0000-\\u0008\\u000B\\u000C\\u000E-\\u001F]")]
    private static partial Regex UnsupportedControlCharactersRegex();

    [GeneratedRegex("\\n[ \\t]*\\n(?:[ \\t]*\\n)+", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessBlankLinesRegex();
}
