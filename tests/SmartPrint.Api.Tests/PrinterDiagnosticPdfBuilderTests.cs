using SmartPrint.Api.Services;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class PrinterDiagnosticPdfBuilderTests
{
    // A compact, valid 1x1 RGB JPEG used only to verify PDF structure and embedding.
    private static readonly byte[] OnePixelJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAn/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAH/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAEFAqf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/AV//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/AV//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAY/Al//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/IT//2Q==");

    [Theory]
    [InlineData("A4", 595.276, 841.89)]
    [InlineData("A3", 841.89, 1190.55)]
    [InlineData("A5", 419.528, 595.276)]
    [InlineData("LETTER", 612d, 792d)]
    [InlineData("LEGAL", 612d, 1008d)]
    public void CreatesReadableSinglePagePdfWithRequestedMediaBox(
        string paperSize, double expectedWidth, double expectedHeight)
    {
        var pdf = PrinterDiagnosticPdfBuilder.CreatePdf([OnePixelJpeg], paperSize);
        using var document = UglyToad.PdfPig.PdfDocument.Open(new MemoryStream(pdf));
        Assert.Single(document.GetPages());
        var page = Assert.Single(document.GetPages());
        Assert.InRange(Math.Abs(page.Width - expectedWidth), 0, 0.01);
        Assert.InRange(Math.Abs(page.Height - expectedHeight), 0, 0.01);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(pdf, 0, 8));
    }

    [Fact]
    public void CreatesTwoPagePdfForOneSheetDuplexDiagnostic()
    {
        var pdf = PrinterDiagnosticPdfBuilder.CreatePdf([OnePixelJpeg, OnePixelJpeg], "A4");
        using var document = UglyToad.PdfPig.PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(2, document.NumberOfPages);
    }

    [Fact]
    public void RejectsUnsupportedPageCountPaperAndMalformedJpeg()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PrinterDiagnosticPdfBuilder.CreatePdf(Array.Empty<byte[]>(), "A4"));
        Assert.Throws<ArgumentException>(() =>
            PrinterDiagnosticPdfBuilder.CreatePdf([OnePixelJpeg], "CUSTOM"));
        Assert.Throws<ArgumentException>(() =>
            PrinterDiagnosticPdfBuilder.CreatePdf([new byte[] { 1, 2, 3 }], "A4"));
    }

    [Fact]
    public void SharedDocumentAnalyzerAcceptsGeneratedDiagnosticPdf()
    {
        var pdf = PrinterDiagnosticPdfBuilder.CreatePdf([OnePixelJpeg], "A4");
        using var stream = new MemoryStream(pdf, writable: false);
        var analysis = DocumentAnalyzer.Analyse(stream, ".pdf");
        Assert.Equal("application/pdf", analysis.MediaType);
        Assert.Equal(1, analysis.PageCount);
    }
}
