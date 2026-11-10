namespace SmartPrint.Agent;

internal readonly record struct IdDocumentPagePlacement(int Left, int Top, int Width, int Height);

internal static class IdDocumentPrintPlacementPolicy
{
    private const double A4WidthMm = 210d;
    private const double A4HeightMm = 297d;
    private const double MaximumNonPrintableMarginMm = 12d;

    public static bool TryCreate(
        int dpiX,
        int dpiY,
        int physicalWidth,
        int physicalHeight,
        int physicalOffsetX,
        int physicalOffsetY,
        int printableWidth,
        int printableHeight,
        int rasterWidth,
        int rasterHeight,
        out IdDocumentPagePlacement placement,
        out string failure)
    {
        placement = default;
        failure = "The printer driver did not report reliable A4 paper metrics.";

        if (dpiX < 150 || dpiY < 150 || Math.Abs(dpiX - dpiY) > 1)
        {
            failure = "The driver reports incompatible horizontal and vertical print resolutions.";
            return false;
        }

        if (physicalWidth <= 0 || physicalHeight <= 0 ||
            printableWidth <= 0 || printableHeight <= 0 ||
            physicalOffsetX < 0 || physicalOffsetY < 0)
            return false;

        // The ID sheet is authored in portrait A4 points. Accept only a physical
        // A4 portrait paper space at the driver's actual dots-per-inch.
        var expectedWidth = A4WidthMm / 25.4d * dpiX;
        var expectedHeight = A4HeightMm / 25.4d * dpiY;
        if (physicalWidth < physicalHeight == false ||
            !WithinTolerance(physicalWidth, expectedWidth, 0.02) ||
            !WithinTolerance(physicalHeight, expectedHeight, 0.02))
        {
            failure = "The selected printer queue is not reporting portrait A4 paper.";
            return false;
        }

        if (!WithinTolerance(rasterWidth, expectedWidth, 0.02) ||
            !WithinTolerance(rasterHeight, expectedHeight, 0.02))
        {
            failure = "The PDF raster size does not match the printer's physical A4 resolution.";
            return false;
        }

        var rightMargin = physicalWidth - physicalOffsetX - printableWidth;
        var bottomMargin = physicalHeight - physicalOffsetY - printableHeight;
        if (rightMargin < 0 || bottomMargin < 0)
        {
            failure = "The printer's reported printable area is outside its physical paper.";
            return false;
        }

        var maximumLeftOffset = MaximumNonPrintableMarginMm / 25.4d * dpiX;
        var maximumTopOffset = MaximumNonPrintableMarginMm / 25.4d * dpiY;
        if (physicalOffsetX > maximumLeftOffset ||
            physicalOffsetY > maximumTopOffset ||
            rightMargin > maximumLeftOffset ||
            bottomMargin > maximumTopOffset)
        {
            failure = "The printer's non-printable margins would cut into the ID sheet's trim guides.";
            return false;
        }

        // GDI's device origin is the top-left of the printable rectangle. Draw
        // the entire A4 page relative to the paper origin, without rescaling it.
        placement = new IdDocumentPagePlacement(
            -physicalOffsetX,
            -physicalOffsetY,
            rasterWidth,
            rasterHeight);
        failure = string.Empty;
        return true;
    }

    private static bool WithinTolerance(double actual, double expected, double fraction) =>
        expected > 0 && Math.Abs(actual - expected) <= expected * fraction;
}
