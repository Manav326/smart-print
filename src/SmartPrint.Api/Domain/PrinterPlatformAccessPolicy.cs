namespace SmartPrint.Api.Domain;

/// <summary>
/// The single platform-level printer access policy.
/// New discoveries are available by default. Only an explicit platform hold
/// blocks selection, diagnostics, assignment and new spooler submissions.
/// </summary>
public static class PrinterPlatformAccessPolicy
{
    public static bool IsAvailable(bool isPlatformHeld) => !isPlatformHeld;

    public static bool CanSelect(string? outputMode, bool isPlatformHeld) =>
        IsAvailable(isPlatformHeld) && PrinterTypePolicy.IsSelectableOutputMode(outputMode);

    public static bool CanRunDiagnostic(string? outputMode, bool isPlatformHeld) =>
        IsAvailable(isPlatformHeld) &&
        (string.Equals(outputMode, PrinterTypePolicy.Physical, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Final Windows spooler boundary. Virtual queues are always digital test
    /// output (TEST_VIRTUAL), never physical fulfilment. Private Printing has
    /// its own central non-production eligibility gate.
    /// </summary>
    public static bool CanAuthorizeSpoolerSubmission(
        string? outputMode,
        string? outputType,
        bool isPlatformHeld,
        string? jobOrigin)
    {
        if (!IsAvailable(isPlatformHeld))
            return false;

        var physicalMode = string.Equals(outputMode, PrinterTypePolicy.Physical, StringComparison.OrdinalIgnoreCase);
        var virtualMode = string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase);
        if (physicalMode)
            return string.Equals(outputType, "PHYSICAL", StringComparison.OrdinalIgnoreCase);

        if (!virtualMode ||
            !string.Equals(outputType, PrivatePrintingOutputPolicy.TestVirtual, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(jobOrigin, "CUSTOMER_QR", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(jobOrigin, "SHOP_OWNER", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(jobOrigin, "PRINTER_DIAGNOSTIC", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanAssign(bool isPlatformHeld) => IsAvailable(isPlatformHeld);
    public static bool CanDispatch(bool isPlatformHeld) => IsAvailable(isPlatformHeld);
}
