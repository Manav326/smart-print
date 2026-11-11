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
        Assert.False(placement.UsesNominalA4Fallback);
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
        Assert.False(placement.UsesNominalA4Fallback);
        Assert.Equal(-leftOffset, placement.Left);
        Assert.Equal(-topOffset, placement.Top);
        Assert.Equal(physicalWidth, placement.Width);
        Assert.Equal(physicalHeight, placement.Height);
        Assert.InRange(placement.Width / (double)printerDpi * 25.4d, 209.98d, 210.02d);
        Assert.InRange(placement.Height / (double)printerDpi * 25.4d, 296.98d, 297.02d);
    }

    [Fact]
    public void UsesNominalA4PlacementInsteadOfRejectingDriverDimensionDrift()
    {
        const int dpi = 300;
        var paperWidth = (int)Math.Round(210d * dpi / 25.4d);
        var paperHeight = (int)Math.Round(297d * dpi / 25.4d);
        var rasterWidth = paperWidth;
        var rasterHeight = paperHeight;

        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            dpi, dpi, dpi,
            paperWidth + 60, paperHeight,
            30, 45,
            paperWidth + 60 - 60, paperHeight - 90,
            rasterWidth, rasterHeight,
            out var placement,
            out var failure);

        Assert.True(success, failure);
        Assert.True(placement.UsesNominalA4Fallback);
        Assert.Equal(0, placement.Left);
        Assert.Equal(0, placement.Top);
        Assert.Equal(paperWidth, placement.Width);
        Assert.Equal(paperHeight, placement.Height);
        Assert.Contains("nominal A4", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsesNominalA4PlacementWhenDriverReportsLetterInsteadOfA4()
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
            out var placement,
            out var failure);

        Assert.True(success, failure);
        Assert.True(placement.UsesNominalA4Fallback);
        Assert.Equal((int)Math.Round(210d * dpi / 25.4d), placement.Width);
        Assert.Equal((int)Math.Round(297d * dpi / 25.4d), placement.Height);
    }

    [Fact]
    public void UsesNominalA4PlacementWhenMarginsWouldPreviouslyBlockTheJob()
    {
        const int dpi = 300;
        var paperWidth = (int)Math.Round(210d * dpi / 25.4d);
        var paperHeight = (int)Math.Round(297d * dpi / 25.4d);
        var excessiveOffset = (int)Math.Ceiling(18d * dpi / 25.4d);

        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            dpi, dpi, dpi,
            paperWidth, paperHeight,
            excessiveOffset, excessiveOffset,
            paperWidth - excessiveOffset - 30,
            paperHeight - excessiveOffset - 60,
            paperWidth, paperHeight,
            out var placement,
            out var failure);

        Assert.True(success, failure);
        Assert.True(placement.UsesNominalA4Fallback);
        Assert.Equal(0, placement.Left);
        Assert.Equal(0, placement.Top);
    }

    [Fact]
    public void MissingDriverPaperDimensionsDoNotBlockNominalA4Placement()
    {
        const int dpi = 300;
        var rasterWidth = (int)Math.Round(210d * dpi / 25.4d);
        var rasterHeight = (int)Math.Round(297d * dpi / 25.4d);

        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            dpi, dpi, dpi,
            0, 0,
            0, 0,
            0, 0,
            rasterWidth, rasterHeight,
            out var placement,
            out var failure);

        Assert.True(success, failure);
        Assert.True(placement.UsesNominalA4Fallback);
        Assert.Equal(rasterWidth, placement.Width);
        Assert.Equal(rasterHeight, placement.Height);
    }

    [Fact]
    public void RejectsRasterScaleDriftBecauseItIsThePreparedDocumentNotDriverMetadata()
    {
        const int dpi = 300;
        var paperWidth = (int)Math.Round(210d * dpi / 25.4d);
        var paperHeight = (int)Math.Round(297d * dpi / 25.4d);

        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            dpi, dpi, dpi,
            paperWidth, paperHeight,
            30, 45,
            paperWidth - 60, paperHeight - 90,
            paperWidth + 4, paperHeight,
            out _, out var failure);

        Assert.False(success);
        Assert.Contains("A4", failure, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("A4", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsInvalidResolutionInsteadOfGuessingAnArbitraryPrintScale()
    {
        var success = IdDocumentPrintPlacementPolicy.TryCreate(
            96, 96, 96,
            0, 0, 0, 0, 0, 0,
            793, 1122,
            out _, out var failure);

        Assert.False(success);
        Assert.Contains("resolution", failure, StringComparison.OrdinalIgnoreCase);
    }
}
