namespace SmartPrint.Api.Domain;

/// <summary>
/// Private Printing may use a virtual PDF destination only as an explicitly
/// enabled non-production test output. A virtual output is never physical fulfilment.
/// </summary>
public static class PrivatePrintingOutputPolicy
{
    public const string Physical = "PHYSICAL";
    public const string PdfExport = "PDF_EXPORT";
    public const string TestVirtual = "TEST_VIRTUAL";

    public static bool IsEligible(bool allowVirtualOutput, bool isProduction, string? outputMode)
    {
        if (string.Equals(outputMode, Physical, StringComparison.OrdinalIgnoreCase)) return true;
        return !isProduction && allowVirtualOutput &&
            string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVirtualTestOutput(bool allowVirtualOutput, bool isProduction, string? outputMode) =>
        !isProduction && allowVirtualOutput &&
        string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase);
}
