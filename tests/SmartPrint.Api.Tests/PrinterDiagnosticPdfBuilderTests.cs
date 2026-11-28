using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class PrinterDiagnosticPdfBuilderTests
{
    private static readonly PrinterDiagnosticArtworkMetadata Metadata = new(
        "Brother HL-L2350DW",
        "Shop-Front-Queue",
        "USB",
        "PHYSICAL",
        "WINDOWS_DRIVER_REPORTED;COLOUR_NOT_CONFIRMED;DUPLEX_NOT_CONFIRMED");

    [Theory]
    [InlineData("A4", 595.276, 841.89)]
    [InlineData("A3", 841.89, 1190.55)]
    [InlineData("A5", 419.528, 595.276)]
    [InlineData("LETTER", 612d, 792d)]
    [InlineData("LEGAL", 612d, 1008d)]
    public void CreatesServerAuthoredSinglePagePdfForRequestedMedia(
        string paperSize, double expectedWidth, double expectedHeight)
    {
        var pdf = Create(paperSize, colour: false, duplex: false);
        using var document = UglyToad.PdfPig.PdfDocument.Open(new MemoryStream(pdf));
        var page = Assert.Single(document.GetPages());
        Assert.InRange(Math.Abs(page.Width - expectedWidth), 0, 0.01);
        Assert.InRange(Math.Abs(page.Height - expectedHeight), 0, 0.01);

        var text = ExtractText(document);
        Assert.Contains("SmartPrint", text, StringComparison.Ordinal);
        Assert.Contains("PRINTER DIAGNOSTIC", text, StringComparison.Ordinal);
        Assert.Contains("Brother HL-L2350DW", text, StringComparison.Ordinal);
        Assert.Contains("Shop-Front-Queue", text, StringComparison.Ordinal);
        Assert.Contains("PAGE GEOMETRY", text, StringComparison.Ordinal);
        Assert.Contains("MONOCHROME", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatesDuplexReverseSideOnlyWhenSettingsExplicitlyRequireIt()
    {
        var pdf = Create("A4", colour: false, duplex: true);
        using var document = UglyToad.PdfPig.PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(2, document.NumberOfPages);
        var text = string.Join(" ", document.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
        Assert.Contains("DUPLEX", text, StringComparison.Ordinal);
        Assert.Contains("REVERSE", text, StringComparison.Ordinal);
        Assert.Contains("REGISTRATION", text, StringComparison.Ordinal);
    }

    [Fact]
    public void IncludesColourBarsOnlyWhenDriverProfileExplicitlySelectsColour()
    {
        var monochromePdf = Create("A4", colour: false, duplex: false);
        using var monochrome = UglyToad.PdfPig.PdfDocument.Open(new MemoryStream(monochromePdf));
        var monoText = ExtractText(monochrome);
        Assert.DoesNotContain("COLOUR CHECK", monoText, StringComparison.Ordinal);
        Assert.Contains("Colour was not explicitly confirmed", monoText, StringComparison.Ordinal);

        var colourPdf = Create("A4", colour: true, duplex: false);
        using var colour = UglyToad.PdfPig.PdfDocument.Open(new MemoryStream(colourPdf));
        Assert.Contains("COLOUR CHECK", ExtractText(colour), StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualDiagnosticArtworkIdentifiesADigitalTestFileNotPhysicalPaper()
    {
        var metadata = Metadata with { OutputType = PrivatePrintingOutputPolicy.TestVirtual };
        var settings = new PrinterDiagnosticSettings(
            "A4", "Mono", false, 1, "VIRTUAL_FILE_OUTPUT;DUPLEX_NOT_APPLICABLE");
        var pdf = PrinterDiagnosticPdfBuilder.CreatePdf(
            settings, metadata, DateTimeOffset.Parse("2026-11-27T12:00:00Z"));

        using var document = UglyToad.PdfPig.PdfDocument.Open(new MemoryStream(pdf));
        var text = ExtractText(document);
        Assert.Contains("VIRTUAL OUTPUT TEST", text, StringComparison.Ordinal);
        Assert.Contains("One generated test file", text, StringComparison.Ordinal);
        Assert.Contains("ONE TEST FILE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ONE COPY", text, StringComparison.Ordinal);
        Assert.Single(document.GetPages());
    }

    [Fact]
    public void SanitizesPrinterMetadataAndDoesNotInterpretItAsPdfOperators()
    {
        var metadata = Metadata with
        {
            PrinterName = "Front printer\n1) Tj /F2 40 Tf",
            QueueName = "Queue (alpha)\\beta"
        };
        var pdf = PrinterDiagnosticPdfBuilder.CreatePdf(
            new PrinterDiagnosticSettings("A4", "Mono", false, 1, "EVIDENCE"),
            metadata,
            DateTimeOffset.Parse("2026-11-27T12:00:00Z"));
        using var document = UglyToad.PdfPig.PdfDocument.Open(new MemoryStream(pdf));
        var text = ExtractText(document);
        Assert.Contains("Front printer?1) Tj /F2 40 Tf", text, StringComparison.Ordinal);
        Assert.Contains("Queue (alpha)\\beta", text, StringComparison.Ordinal);
        Assert.DoesNotContain("40 Tf", text.Replace("Front printer?1) Tj /F2 40 Tf", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedDocumentAnalyzerAcceptsGeneratedServerPdf()
    {
        var pdf = Create("A4", colour: true, duplex: true);
        using var stream = new MemoryStream(pdf, writable: false);
        var analysis = DocumentAnalyzer.Analyse(stream, ".pdf");
        Assert.Equal("application/pdf", analysis.MediaType);
        Assert.Equal(2, analysis.PageCount);
    }

    [Fact]
    public void RejectsUnsupportedMediaAndInconsistentCapabilityPageCount()
    {
        Assert.Throws<ArgumentException>(() =>
            PrinterDiagnosticPdfBuilder.CreatePdf(
                new PrinterDiagnosticSettings("CUSTOM", "Mono", false, 1, "EVIDENCE"),
                Metadata,
                DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            PrinterDiagnosticPdfBuilder.CreatePdf(
                new PrinterDiagnosticSettings("A4", "Mono", true, 1, "EVIDENCE"),
                Metadata,
                DateTimeOffset.UtcNow));
    }

    private static byte[] Create(string paperSize, bool colour, bool duplex) =>
        PrinterDiagnosticPdfBuilder.CreatePdf(
            new PrinterDiagnosticSettings(
                paperSize,
                colour ? "Colour" : "Mono",
                duplex,
                duplex ? 2 : 1,
                "WINDOWS_DRIVER_REPORTED"),
            Metadata,
            DateTimeOffset.Parse("2026-11-27T12:00:00Z"));

    private static string ExtractText(UglyToad.PdfPig.PdfDocument document) =>
        string.Join(" ", document.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
}
