namespace SmartPrint.Agent;

internal readonly record struct IdDocumentPagePlacement(
    int Left,
    int Top,
    int Width,
    int Height,
    bool UsesNominalA4Fallback = false);

/// <summary>
/// Maps the already-validated A4 ID sheet to nominal physical A4 coordinates.
/// Driver-reported paper dimensions and printable margins are advisory only:
/// unreliable values must not prevent a physical print submission.
/// </summary>
internal static class IdDocumentPrintPlacementPolicy
{
    private const double A4WidthMm = 210d;
    private const double A4HeightMm = 297d;
    private const double DriverMetricToleranceMm = 3d;
    private const double MaximumTrustedMarginMm = 15d;

    public static bool TryCreate(
        int dpiX,
        int dpiY,
        int rasterDpi,
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
        failure = string.Empty;

        // Printer DPI is needed to map the page into device pixels. Windows
        // driver DPI values outside this range are normalized by the caller
        // before reaching this policy; do not trust unbounded values here.
        if (dpiX is < 150 or > 2400 || dpiY is < 150 or > 2400 ||
            rasterDpi is < 150 or > 300)
        {
            failure = "The printer or PDF rasterizer did not provide a usable resolution.";
            return false;
        }

        // This validates our own PDF raster, not the printer driver. A malformed
        // or non-A4 ID sheet is still unsafe to position as a physical ID sheet.
        var expectedRasterWidth = A4WidthMm / 25.4d * rasterDpi;
        var expectedRasterHeight = A4HeightMm / 25.4d * rasterDpi;
        if (!WithinTolerance(rasterWidth, expectedRasterWidth, rasterDpi, 0.25d) ||
            !WithinTolerance(rasterHeight, expectedRasterHeight, rasterDpi, 0.25d))
        {
            failure = "The prepared ID sheet is not an A4 portrait page.";
            return false;
        }

        // Never use driver paper width/height to scale the content. Generate
        // the destination from the requested A4 physical dimensions and the
        // printer's usable DPI. This prevents driver-size drift from changing
        // the default 85.6 × 53.98 mm card geometry.
        var nominalWidth = checked((int)Math.Round(A4WidthMm / 25.4d * dpiX));
        var nominalHeight = checked((int)Math.Round(A4HeightMm / 25.4d * dpiY));
        if (nominalWidth <= 0 || nominalHeight <= 0)
        {
            failure = "The nominal A4 destination dimensions are invalid.";
            return false;
        }

        var rightMargin = (long)physicalWidth - physicalOffsetX - printableWidth;
        var bottomMargin = (long)physicalHeight - physicalOffsetY - printableHeight;
        var maximumHorizontalMargin = MaximumTrustedMarginMm / 25.4d * dpiX;
        var maximumVerticalMargin = MaximumTrustedMarginMm / 25.4d * dpiY;

        var driverGeometryIsTrustworthy =
            physicalWidth > 0 &&
            physicalHeight > 0 &&
            physicalWidth < physicalHeight &&
            WithinTolerance(physicalWidth, A4WidthMm / 25.4d * dpiX, dpiX, DriverMetricToleranceMm) &&
            WithinTolerance(physicalHeight, A4HeightMm / 25.4d * dpiY, dpiY, DriverMetricToleranceMm) &&
            physicalOffsetX >= 0 &&
            physicalOffsetY >= 0 &&
            physicalOffsetX <= maximumHorizontalMargin &&
            physicalOffsetY <= maximumVerticalMargin &&
            printableWidth > 0 &&
            printableHeight > 0 &&
            rightMargin >= 0 &&
            bottomMargin >= 0 &&
            rightMargin <= maximumHorizontalMargin &&
            bottomMargin <= maximumVerticalMargin;

        // Device origins are relative to the printable rectangle. Only apply
        // the reported physical offset when all of the driver's geometry is
        // internally consistent. Otherwise, draw at the nominal page origin.
        // ID faces sit well inside the centred A4 sheet, so this best-effort
        // origin remains printable on ordinary non-borderless printers.
        var left = driverGeometryIsTrustworthy ? -physicalOffsetX : 0;
        var top = driverGeometryIsTrustworthy ? -physicalOffsetY : 0;
        placement = new IdDocumentPagePlacement(
            left,
            top,
            nominalWidth,
            nominalHeight,
            UsesNominalA4Fallback: !driverGeometryIsTrustworthy);

        if (!driverGeometryIsTrustworthy)
        {
            failure =
                "The printer driver reported inconsistent paper dimensions or margins. " +
                "SmartPrint will submit the ID sheet using nominal A4 coordinates; " +
                "the final physical size depends on the printer's actual output scaling.";
        }

        return true;
    }

    private static bool WithinTolerance(double actual, double expected, int dpi, double toleranceMm) =>
        expected > 0 &&
        dpi > 0 &&
        Math.Abs(actual - expected) <= Math.Max(1d, dpi * toleranceMm / 25.4d);
}
