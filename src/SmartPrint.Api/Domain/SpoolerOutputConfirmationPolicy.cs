namespace SmartPrint.Api.Domain;

/// <summary>
/// Allows physical output confirmation only from a recent Windows spooler observation
/// or an explicit, audited shop-operator reconciliation after an interrupted submission.
/// </summary>
public static class SpoolerOutputConfirmationPolicy
{
    // The Agent monitors spooler jobs on a configurable 10-300 second loop. Ten minutes
    // allows two maximum-length cycles while ensuring an offline Agent cannot leave a
    // stale QUEUED/PRINTED observation authoritative indefinitely.
    public static readonly TimeSpan MaximumSpoolerObservationAge = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> VerifiedAgentSpoolerStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "RETAINED", "OFFLINE", "PAPEROUT", "PAUSED", "ERROR", "USERINTERVENTION",
        "PRINTING", "SPOOLING", "PRINTED", "QUEUED", "OUTPUT_UNCONFIRMED"
    };

    private static readonly HashSet<string> PrinterWarningStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "OFFLINE", "PAUSED"
    };

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    public static bool IsRetained(string? spoolerStatus) =>
        string.Equals(Normalize(spoolerStatus), "RETAINED", StringComparison.OrdinalIgnoreCase);

    public static bool IsPhysicalOutputConfirmationStatus(string? jobStatus) =>
        Normalize(jobStatus).ToUpperInvariant() is "SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED";

    public static bool IsFreshSpoolerObservation(
        DateTimeOffset? spoolerSubmittedUtc,
        DateTimeOffset? spoolerObservedUtc,
        DateTimeOffset nowUtc)
    {
        if (spoolerSubmittedUtc is null || spoolerObservedUtc is null)
            return false;

        var submitted = spoolerSubmittedUtc.Value;
        var observed = spoolerObservedUtc.Value;
        return observed >= submitted &&
            observed <= nowUtc &&
            nowUtc - observed <= MaximumSpoolerObservationAge;
    }

    public static bool HasVerifiedAgentSpoolerObservation(
        string? spoolerStatus,
        DateTimeOffset? spoolerSubmittedUtc,
        DateTimeOffset? spoolerObservedUtc,
        DateTimeOffset nowUtc,
        string? failureCode = null,
        int? spoolerJobId = null)
    {
        var normalizedSpoolerStatus = Normalize(spoolerStatus);
        return IsFreshSpoolerObservation(spoolerSubmittedUtc, spoolerObservedUtc, nowUtc) &&
            VerifiedAgentSpoolerStatuses.Contains(normalizedSpoolerStatus) &&
            (!string.Equals(normalizedSpoolerStatus, "OUTPUT_UNCONFIRMED", StringComparison.OrdinalIgnoreCase) ||
                IsVerifiedSpoolerAbsenceReport(failureCode, spoolerJobId));
    }

    public static bool CanConfirmPhysicalOutput(
        string? jobStatus,
        string? spoolerStatus,
        DateTimeOffset? spoolerSubmittedUtc,
        DateTimeOffset? spoolerObservedUtc,
        DateTimeOffset nowUtc,
        string? printerStatus,
        string? failureCode = null,
        int? spoolerJobId = null)
    {
        var normalizedJobStatus = Normalize(jobStatus);
        var normalizedSpoolerStatus = Normalize(spoolerStatus);

        if (!IsPhysicalOutputConfirmationStatus(normalizedJobStatus) ||
            !HasVerifiedAgentSpoolerObservation(
                normalizedSpoolerStatus, spoolerSubmittedUtc, spoolerObservedUtc,
                nowUtc, failureCode, spoolerJobId) ||
            IsRetained(normalizedSpoolerStatus))
            return false;

        // A live/queued Windows job is not proof of printed paper. Require the
        // terminal PRINTED state, or a verified queue-absence report followed by
        // the operator's existing physical-page confirmation.
        if (string.Equals(normalizedSpoolerStatus, "OUTPUT_UNCONFIRMED", StringComparison.OrdinalIgnoreCase))
            return string.Equals(normalizedJobStatus, "OUTPUT_UNCONFIRMED", StringComparison.OrdinalIgnoreCase) &&
                IsVerifiedSpoolerAbsenceReport(failureCode, spoolerJobId);

        if (!string.Equals(normalizedSpoolerStatus, "PRINTED", StringComparison.OrdinalIgnoreCase))
            return false;

        return !PrinterWarningStatuses.Contains(Normalize(printerStatus));
    }

    /// <summary>
    /// The Agent restarted before Windows returned a spooler ID. This is uncertain,
    /// not a verified absence. Permit a separate operator assertion only when the
    /// authenticated Agent supplied the exact queue and document name to inspect.
    /// RETAINED and terminal PRINTED states are not manual-reconciliation cases.
    /// </summary>
    public static bool CanManuallyReconcileUncertainOutput(
        string? jobStatus,
        string? spoolerStatus,
        string? failureCode,
        int? spoolerJobId,
        string? queueName,
        string? spoolerJobName)
    {
        return string.Equals(Normalize(jobStatus), "OUTPUT_UNCONFIRMED", StringComparison.OrdinalIgnoreCase) &&
            !IsRetained(spoolerStatus) &&
            !string.Equals(Normalize(spoolerStatus), "PRINTED", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Normalize(failureCode), "AGENT_RESTART_DURING_PRINT_SUBMISSION", StringComparison.OrdinalIgnoreCase) &&
            spoolerJobId is null &&
            !string.IsNullOrWhiteSpace(queueName) &&
            !string.IsNullOrWhiteSpace(spoolerJobName);
    }

    public static bool IsVerifiedSpoolerAbsenceReport(string? reasonCode, int? spoolerJobId) =>
        spoolerJobId is > 0 &&
        string.Equals(Normalize(reasonCode), "SPOOLER_JOB_NO_LONGER_VISIBLE", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldPreserveLastKnownRetainedStatus(string? currentStatus, string? incomingStatus) =>
        IsRetained(currentStatus) &&
        string.Equals(Normalize(incomingStatus), "PRINTER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
}
