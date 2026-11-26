namespace SmartPrint.Api.Domain;

public static class PrinterDispatchRules
{
    // The "Microsoft Print to PDF" destination is rendered by the paired Agent
    // into its protected output directory. It is not submitted to a Windows
    // virtual spooler queue, so WMI inventory freshness is not a dispatch input.
    public static bool IsCurrentSpoolerSubmissionAttempt(
        string? currentSpoolerJobName,
        string? requestedSpoolerJobName,
        DateTimeOffset? leaseExpiresUtc,
        DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(currentSpoolerJobName) &&
        !string.IsNullOrWhiteSpace(requestedSpoolerJobName) &&
        string.Equals(currentSpoolerJobName, requestedSpoolerJobName, StringComparison.Ordinal) &&
        leaseExpiresUtc is DateTimeOffset lease && lease > now;

    public static bool CanDispatchWithStaleInventory(string? outputMode) =>
        string.Equals(outputMode, "PDF_EXPORT", StringComparison.OrdinalIgnoreCase);

    public static bool IsDispatchableStatus(
        string? outputMode,
        string? status,
        bool virtualTestGrantActive,
        bool isProduction)
    {
        var readyOrBusy = string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Busy", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase))
            return !isProduction && virtualTestGrantActive && readyOrBusy;

        if (string.Equals(outputMode, PrinterTypePolicy.Physical, StringComparison.OrdinalIgnoreCase))
            return readyOrBusy;

        // Older Agents may have recorded Unknown before PDF_EXPORT became
        // a first-class output mode. The API also requires a live Agent heartbeat.
        return CanDispatchWithStaleInventory(outputMode) &&
               (readyOrBusy || string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase));
    }
}
