namespace SmartPrint.Api.Domain;

/// <summary>
/// Private Printing keeps its protected-source guarantees. Physical printers
/// are allowed; digital destinations require the central non-production
/// setting and are never represented as physical fulfilment.
/// </summary>
public static class PrivatePrintingOutputPolicy
{
    public const string Physical = "PHYSICAL";
    public const string PdfExport = "PDF_EXPORT";
    public const string TestVirtual = "TEST_VIRTUAL";

    public static bool IsEligible(bool allowVirtualOutput, bool isProduction, string? outputMode)
    {
        if (string.Equals(outputMode, Physical, StringComparison.OrdinalIgnoreCase))
            return true;

        if (isProduction || !allowVirtualOutput)
            return false;

        return string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVirtualTestOutput(bool allowVirtualOutput, bool isProduction, string? outputMode) =>
        !isProduction &&
        allowVirtualOutput &&
        (string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase));
}
