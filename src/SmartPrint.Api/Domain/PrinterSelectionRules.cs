namespace SmartPrint.Api.Domain;

/// <summary>
/// A printer may consume a shop plan slot only when its owning Agent and the
/// Windows queue have both reported recently and the queue can accept work.
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
        DateTimeOffset now)
    {
        var freshAfter = now.AddMinutes(-2);
        if (!agentOnline || agentRevoked ||
            agentLastSeenUtc is null || agentLastSeenUtc < freshAfter ||
            printerLastSeenUtc is null || printerLastSeenUtc < freshAfter)
            return false;

        if (!string.Equals(outputMode, "PHYSICAL", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputMode, "PDF_EXPORT", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(printerStatus, "Ready", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(printerStatus, "Busy", StringComparison.OrdinalIgnoreCase);
    }
}
