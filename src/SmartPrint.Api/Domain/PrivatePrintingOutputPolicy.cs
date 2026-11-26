namespace SmartPrint.Api.Domain;

/// <summary>
/// Private Printing may use PDF Export only when the central non-production
/// setting is enabled. Other virtual Windows queues require their own current
/// shop-scoped grant. Neither virtual path is physical fulfilment.
/// </summary>
public static class PrivatePrintingOutputPolicy
{
    public const string Physical = "PHYSICAL";
    public const string PdfExport = "PDF_EXPORT";
    public const string TestVirtual = "TEST_VIRTUAL";

    public static bool IsEligible(
        bool allowVirtualOutput,
        bool isProduction,
        string? outputMode,
        bool virtualTestGrantActive = false)
    {
        if (string.Equals(outputMode, Physical, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase))
            return !isProduction && virtualTestGrantActive;
        return !isProduction && allowVirtualOutput &&
            string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVirtualTestOutput(
        bool allowVirtualOutput,
        bool isProduction,
        string? outputMode,
        bool virtualTestGrantActive = false) =>
        !isProduction &&
        ((allowVirtualOutput && string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase)) ||
         (virtualTestGrantActive && string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase)));
}
