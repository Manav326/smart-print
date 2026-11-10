using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class IdDocumentPrintPlacementPolicyTests
{
    [Fact]
    public void MapsA4PageAt300DpiToPhysicalPaperOriginWithoutShrinkingToPrintableArea()
    {
        const int dpi = 300;
        var paperWidth = (int)Math.Round(210d * dpi / 25.4d);
        var paperHeight = (int)Math.Round(297d * dpi / 25.4d);
        var rasterWidth = paperWidth;
        var rasterHeight = paperHeight;
        const int leftOffset = 30;
        const int topOffset = 45;
        const int rightMargin = 30;
        const int bottomMargin = 60;

        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            dpi, dpi, dpi,
            paperWidth, paperHeight,
            leftOffset, topOffset,
            paperWidth - leftOffset - rightMargin,
            paperHeight - topOffset - bottomMargin,
            rasterWidth, rasterHeight,
            out var placement,
            out var failure);

        Assert.True(success, failure);
        Assert.Equal(-leftOffset, placement.Left);
        Assert.Equal(-topOffset, placement.Top);
        Assert.Equal(paperWidth, placement.Width);
        Assert.Equal(paperHeight, placement.Height);
    }

    [Fact]
    public void Maps300DpiPdfTo600DpiA4PaperWithoutChangingPhysicalSize()
    {
        const int printerDpi = 600;
        const int rasterDpi = 300;
        var physicalWidth = (int)Math.Round(210d * printerDpi / 25.4d);
        var physicalHeight = (int)Math.Round(297d * printerDpi / 25.4d);
        var rasterWidth = (int)Math.Round(210d * rasterDpi / 25.4d);
        var rasterHeight = (int)Math.Round(297d * rasterDpi / 25.4d);
        const int leftOffset = 60;
        const int topOffset = 90;
        const int rightMargin = 60;
        const int bottomMargin = 90;

        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            printerDpi, printerDpi, rasterDpi,
            physicalWidth, physicalHeight,
            leftOffset, topOffset,
            physicalWidth - leftOffset - rightMargin,
            physicalHeight - topOffset - bottomMargin,
            rasterWidth, rasterHeight,
            out var placement,
            out var failure);

        Assert.True(success, failure);
        Assert.Equal(-leftOffset, placement.Left);
        Assert.Equal(-topOffset, placement.Top);
        Assert.Equal(physicalWidth, placement.Width);
        Assert.Equal(physicalHeight, placement.Height);

        // The 300-dpi source raster is scaled only to the printer's 600-dpi
        // physical pixel grid; the result is still exactly 210 × 297 mm.
        Assert.InRange(placement.Width / (double)printerDpi * 25.4d, 209.98d, 210.02d);
        Assert.InRange(placement.Height / (double)printerDpi * 25.4d, 296.98d, 297.02d);
    }

    [Fact]
    public void RejectsLetterPaperInsteadOfShrinkingAnIdSheet()
    {
        const int dpi = 300;
        var letterWidth = (int)Math.Round(215.9d * dpi / 25.4d);
        var letterHeight = (int)Math.Round(279.4d * dpi / 25.4d);
        var rasterWidth = (int)Math.Round(210d * dpi / 25.4d);
        var rasterHeight = (int)Math.Round(297d * dpi / 25.4d);

        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            dpi, dpi, dpi,
            letterWidth, letterHeight,
            30, 45, letterWidth - 60, letterHeight - 90,
            rasterWidth, rasterHeight,
            out _, out var failure);

        Assert.False(success);
        Assert.Contains("A4", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsMarginsThatWouldCutIntoTheCardGuides()
    {
        const int dpi = 300;
        var paperWidth = (int)Math.Round(210d * dpi / 25.4d);
        var paperHeight = (int)Math.Round(297d * dpi / 25.4d);
        var rasterWidth = paperWidth;
        var rasterHeight = paperHeight;
        var excessiveOffset = (int)Math.Ceiling(10.5d * dpi / 25.4d);

        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            dpi, dpi, dpi,
            paperWidth, paperHeight,
            excessiveOffset, 45,
            paperWidth - excessiveOffset - 30,
            paperHeight - 45 - 60,
            rasterWidth, rasterHeight,
            out _, out var failure);

        Assert.False(success);
        Assert.Contains("margins", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsPageRasterThatIsNotA4AtTheDeclaredRasterResolution()
    {
        const int dpi = 300;
        var paperWidth = (int)Math.Round(210d * dpi / 25.4d);
        var paperHeight = (int)Math.Round(297d * dpi / 25.4d);

        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            dpi, dpi, dpi,
            paperWidth, paperHeight,
            30, 45,
            paperWidth - 60, paperHeight - 90,
            paperWidth - 120, paperHeight,
            out _, out var failure);

        Assert.False(success);
        Assert.Contains("raster", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsMissingOrInvalidPhysicalPaperMetrics()
    {
        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            300, 300, 300,
            0, 0,
            0, 0,
            0, 0,
            2480, 3508,
            out _, out _);

        Assert.False(success);
    }
}
