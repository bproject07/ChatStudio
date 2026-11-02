using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SearchQuality.Corpus;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SearchQuality.Tests.Corpus;

public sealed class DocumentTextExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ReadsDocxHeadingAndBody()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "policy.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(
                       path,
                       WordprocessingDocumentType.Document))
            {
                var mainPart = document.AddMainDocumentPart();
                mainPart.Document = new Document(new Body(
                    new Paragraph(
                        new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                        new Run(new Text("Правила за достъп"))),
                    new Paragraph(new Run(new Text("Достъпът се одобрява от ръководител.")))));
            }

            var extracted = await DocumentTextExtractor.ExtractAsync(
                path,
                TestContext.Current.CancellationToken);

            Assert.Equal("Правила за достъп", extracted.Title);
            Assert.Contains("# Правила за достъп", extracted.Content);
            Assert.Contains("Достъпът се одобрява от ръководител.", extracted.Content);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExtractAsync_ReadsPdfInContentOrder()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "policy.pdf");
        try
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText(
                "Remote work is allowed three days per week.",
                12,
                new PdfPoint(40, 760),
                font);
            await File.WriteAllBytesAsync(
                path,
                builder.Build(),
                TestContext.Current.CancellationToken);

            var extracted = await DocumentTextExtractor.ExtractAsync(
                path,
                TestContext.Current.CancellationToken);

            Assert.Equal("policy", extracted.Title);
            Assert.Contains("Remote work is allowed three days per week.", extracted.Content);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData("document.md", true)]
    [InlineData("document.txt", true)]
    [InlineData("document.pdf", true)]
    [InlineData("document.docx", true)]
    [InlineData("document.doc", false)]
    [InlineData("document.csv", false)]
    public void IsSupported_RecognizesConfiguredFormats(string fileName, bool expected)
    {
        Assert.Equal(expected, DocumentTextExtractor.IsSupported(fileName));
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "search-quality-extractor-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
