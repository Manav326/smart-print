namespace SmartPrint.Api.Domain;

/// <summary>
/// Keeps physical output confirmation aligned with the latest Windows spooler
/// observation. A retained Windows job must not be marked collected or purged.
/// </summary>
public static class SpoolerOutputConfirmationPolicy
{
    public static bool IsRetained(string? spoolerStatus) =>
        string.Equals(spoolerStatus?.Trim(), "RETAINED", StringComparison.OrdinalIgnoreCase);

    public static bool CanConfirmPhysicalOutput(string? spoolerStatus) =>
        !IsRetained(spoolerStatus);

    public static bool ShouldPreserveLastKnownRetainedStatus(string? currentStatus, string? incomingStatus) =>
        IsRetained(currentStatus) &&
        string.Equals(incomingStatus?.Trim(), "PRINTER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
}
