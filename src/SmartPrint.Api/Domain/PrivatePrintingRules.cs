namespace SmartPrint.Api.Domain;

/// <summary>
/// Server-side invariants for customer-selected private printing. This is a
/// retention mode, not an anonymous payment or untraceable transaction mode.
/// </summary>
public static class PrivatePrintingRules
{
    public static bool RequiresLocalAgent(bool privatePrinting) => privatePrinting;
    public static bool AllowsCloudDocumentStorage(bool privatePrinting) => !privatePrinting;
    public static bool AllowsManagedPdfExport(bool privatePrinting) => !privatePrinting;

    public static bool ShouldEraseLocalSource(
        bool privatePrinting,
        string jobStatus,
        string outputType,
        DateTimeOffset? outputConfirmedUtc)
    {
        return privatePrinting &&
            string.Equals(jobStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(outputType, "PHYSICAL", StringComparison.OrdinalIgnoreCase) &&
            outputConfirmedUtc is not null;
    }

    public static string SafeDocumentLabel(bool privatePrinting, string originalFileName) =>
        privatePrinting ? "Private document" : originalFileName;
}