namespace SmartPrint.Api.Domain;

public static class PrinterDispatchRules
{
    // Microsoft Print to PDF is rendered by the paired Agent into its protected
    // export folder; it is not submitted to a Windows virtual spooler queue.
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
        string.Equals(outputMode, PrinterTypePolicy.PdfExport, StringComparison.OrdinalIgnoreCase);

    public static bool IsDispatchableStatus(
        string? outputMode,
        string? status,
        bool isPlatformHeld = false)
    {
        if (!PrinterPlatformAccessPolicy.CanDispatch(isPlatformHeld))
            return false;

        var readyOrBusy = PrinterTypePolicy.IsWorkingWindowsQueue(status);
        if (string.Equals(outputMode, PrinterTypePolicy.Physical, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase))
            return readyOrBusy;

        return CanDispatchWithStaleInventory(outputMode) &&
               (readyOrBusy || string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase));
    }
}
