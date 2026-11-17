namespace SmartPrint.Agent;

internal static class PrinterCapabilityRules
{
    // Windows DeviceCapabilities returns zero for an explicit negative result
    // and a negative error result when the driver cannot report a value.
    internal static bool IsExplicitlyUnsupportedCapability(int reportedValue) =>
        reportedValue == 0;

    // A paper list is authoritative only when Windows supplied a complete,
    // bounded list. Missing, failed or inconsistent reports remain unknown.
    internal static bool? EvaluatePaperSupport(
        int reportedCount,
        int actualCount,
        IReadOnlyList<short> reportedPapers,
        short requestedPaperCode)
    {
        if (reportedCount <= 0 ||
            reportedCount > 256 ||
            actualCount != reportedCount ||
            reportedPapers.Count != actualCount)
        {
            return null;
        }

        return reportedPapers.Contains(requestedPaperCode);
    }

    internal static bool SupportsPagesPerSheet(IReadOnlyCollection<int> supportedValues, int requestedValue) =>
        requestedValue >= 1 && (requestedValue == 1 || supportedValues.Contains(requestedValue));

    internal static bool IsRequestedOrientationAccepted(
        bool orientationFieldReported,
        short actualOrientation,
        short requestedOrientation) =>
        orientationFieldReported && actualOrientation == requestedOrientation;
}
