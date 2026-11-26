namespace SmartPrint.Api.Domain;

/// <summary>
/// A printer may consume a shop plan slot only when its owning Agent is online.
/// Physical queues also need a recent Ready/Busy Windows report. PDF Export is
/// rendered by the Agent into its protected export folder and does not depend
/// on the Microsoft Print to PDF Windows queue's own status or scan timestamp.
/// Visibility and selection are deliberately separate.
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
        bool virtualTestGrantActive = false)
    {
        var freshAfter = now.AddMinutes(-2);
        if (!agentOnline || agentRevoked ||
            agentLastSeenUtc is null || agentLastSeenUtc < freshAfter)
            return false;

        if (string.Equals(outputMode, "PDF_EXPORT", StringComparison.OrdinalIgnoreCase))
            return true;

        var physical = string.Equals(outputMode, "PHYSICAL", StringComparison.OrdinalIgnoreCase);
        var authorisedVirtual = string.Equals(outputMode, "UNSUPPORTED_VIRTUAL", StringComparison.OrdinalIgnoreCase) &&
            virtualTestGrantActive;
        if (!physical && !authorisedVirtual)
            return false;

        if (printerLastSeenUtc is null || printerLastSeenUtc < freshAfter)
            return false;

        return string.Equals(printerStatus, "Ready", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(printerStatus, "Busy", StringComparison.OrdinalIgnoreCase);
    }
}
