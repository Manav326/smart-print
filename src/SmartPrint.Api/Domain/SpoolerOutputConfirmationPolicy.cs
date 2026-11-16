namespace SmartPrint.Api.Domain;

/// <summary>
/// Keeps physical output confirmation aligned with the latest Windows spooler
/// observation. A retained Windows job must not be marked collected or purged.
/// </summary>
public static class SpoolerOutputConfirmationPolicy
{
    public static bool IsRetained(string? spoolerStatus) =>
        string.Equals(spoolerStatus?.Trim(), "RETAINED", StringComparison.OrdinalIgnoreCase);

    public static bool IsPhysicalOutputConfirmationStatus(string? jobStatus) =>
        jobStatus?.Trim().ToUpperInvariant() is "SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED";

    public static bool CanConfirmPhysicalOutput(string? jobStatus, string? spoolerStatus) =>
        IsPhysicalOutputConfirmationStatus(jobStatus) && !IsRetained(spoolerStatus);

    public static bool ShouldPreserveLastKnownRetainedStatus(string? currentStatus, string? incomingStatus) =>
        IsRetained(currentStatus) &&
        string.Equals(incomingStatus?.Trim(), "PRINTER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
}
