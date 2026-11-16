namespace SmartPrint.Api.Domain;

/// <summary>
/// Keeps physical output confirmation aligned with the latest verified Windows
/// spooler observation. A retained Windows job must not be marked collected or purged.
/// </summary>
public static class SpoolerOutputConfirmationPolicy
{
    private static readonly HashSet<string> VerifiedAgentSpoolerStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "RETAINED",
        "OFFLINE",
        "PAPEROUT",
        "PAUSED",
        "ERROR",
        "USERINTERVENTION",
        "PRINTING",
        "SPOOLING",
        "PRINTED",
        "QUEUED",
        "OUTPUT_UNCONFIRMED"
    };

    private static readonly HashSet<string> SpoolerWarningStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "OFFLINE",
        "PAPEROUT",
        "PAUSED",
        "ERROR",
        "USERINTERVENTION"
    };

    private static readonly HashSet<string> PrinterWarningStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "OFFLINE",
        "PAUSED"
    };

    public static bool IsRetained(string? spoolerStatus) =>
        string.Equals(spoolerStatus?.Trim(), "RETAINED", StringComparison.OrdinalIgnoreCase);

    public static bool IsPhysicalOutputConfirmationStatus(string? jobStatus) =>
        jobStatus?.Trim().ToUpperInvariant() is "SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED";

    public static bool HasVerifiedAgentSpoolerObservation(string? spoolerStatus, DateTimeOffset? spoolerObservedUtc) =>
        spoolerObservedUtc is not null &&
        VerifiedAgentSpoolerStatuses.Contains(spoolerStatus?.Trim() ?? string.Empty);

    public static bool CanConfirmPhysicalOutput(
        string? jobStatus,
        string? spoolerStatus,
        DateTimeOffset? spoolerObservedUtc,
        string? printerStatus)
    {
        var normalizedJobStatus = jobStatus?.Trim() ?? string.Empty;
        var normalizedSpoolerStatus = spoolerStatus?.Trim() ?? string.Empty;

        if (!IsPhysicalOutputConfirmationStatus(normalizedJobStatus) ||
            !HasVerifiedAgentSpoolerObservation(normalizedSpoolerStatus, spoolerObservedUtc) ||
            IsRetained(normalizedSpoolerStatus))
            return false;

        // OUTPUT_UNCONFIRMED is the deliberate manual-resolution path: the shop
        // checks the actual pages even when Windows reported a queue warning.
        if (string.Equals(normalizedJobStatus, "OUTPUT_UNCONFIRMED", StringComparison.OrdinalIgnoreCase))
            return true;

        return !SpoolerWarningStatuses.Contains(normalizedSpoolerStatus) &&
            !PrinterWarningStatuses.Contains(printerStatus?.Trim() ?? string.Empty);
    }

    public static bool IsVerifiedSpoolerAbsenceReport(string? reasonCode, int? spoolerJobId)
    {
        var normalizedReason = reasonCode?.Trim();
        return (spoolerJobId is > 0 &&
                string.Equals(normalizedReason, "SPOOLER_JOB_NO_LONGER_VISIBLE", StringComparison.OrdinalIgnoreCase)) ||
            (spoolerJobId is null &&
                string.Equals(normalizedReason, "AGENT_RESTART_DURING_PRINT_SUBMISSION", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldPreserveLastKnownRetainedStatus(string? currentStatus, string? incomingStatus) =>
        IsRetained(currentStatus) &&
        string.Equals(incomingStatus?.Trim(), "PRINTER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
}
