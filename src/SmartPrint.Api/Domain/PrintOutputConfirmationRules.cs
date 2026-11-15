namespace SmartPrint.Api.Domain;

public static class PrintOutputConfirmationRules
{
    public static bool IsSpoolerJobRetained(string? spoolerStatus) =>
        string.Equals(spoolerStatus?.Trim(), "RETAINED", StringComparison.OrdinalIgnoreCase);
}
