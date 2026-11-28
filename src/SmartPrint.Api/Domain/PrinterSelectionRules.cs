namespace SmartPrint.Api.Domain;

/// <summary>
/// A queue can consume a shop plan slot only when its paired Agent is online.
/// Physical and virtual Windows queues need a fresh Ready/Busy inventory report.
/// PDF Export is a separate Agent-side file export and does not use queue readiness.
/// Platform holds are checked by the central PrinterPlatformAccessPolicy.
/// </summary>
public static class PrinterSelectionRules
{
    public static bool CanSelect(
        string? outputMode,
        string? printerStatus,
        DateTimeOffset? printerLastSeenUtc,
        bool agentOnline,
        bool agentRevoked,
        DateTimeOffset? agentLastSeenUtc,
        DateTimeOffset now,
        bool isPlatformHeld)
    {
        var freshAfter = now.AddMinutes(-2);
        if (!PrinterPlatformAccessPolicy.CanSelect(outputMode, isPlatformHeld) ||
            !agentOnline || agentRevoked ||
            agentLastSeenUtc is null || agentLastSeenUtc < freshAfter)
            return false;

        if (string.Equals(outputMode, PrinterTypePolicy.PdfExport, StringComparison.OrdinalIgnoreCase))
            return true;

        if (printerLastSeenUtc is null || printerLastSeenUtc < freshAfter)
            return false;

        return PrinterTypePolicy.IsWorkingWindowsQueue(printerStatus);
    }
}
