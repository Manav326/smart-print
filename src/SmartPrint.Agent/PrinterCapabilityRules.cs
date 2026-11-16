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


    internal static short? SelectPhotoMediaTypeCode(
        IReadOnlyList<(short Code, string Name)> mediaTypes,
        string finish)
    {
        var requested = finish.Trim().ToUpperInvariant() switch
        {
            "GLOSSY" => "GLOSS",
            "MATTE" => "MATTE",
            _ => null
        };
        if (requested is null || mediaTypes.Count == 0)
            return null;

        var matches = mediaTypes
            .Where(item =>
            {
                var name = new string((item.Name ?? string.Empty)
                    .Where(char.IsLetterOrDigit)
                    .Select(char.ToUpperInvariant)
                    .ToArray());
                return requested == "GLOSS"
                    ? name.Contains("GLOSS", StringComparison.Ordinal)
                    : name.Contains("MATTE", StringComparison.Ordinal);
            })
            .Select(item => item.Code)
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static short? SelectFourBySixPaperCode(
        IReadOnlyList<(short Code, string Name, int WidthTenthMm, int HeightTenthMm)> papers)
    {
        const int expectedWidth = 1016;
        const int expectedHeight = 1524;
        const int tolerance = 5;
        var matches = papers
            .Where(item =>
            {
                var exactDimensions =
                    Math.Abs(item.WidthTenthMm - expectedWidth) <= tolerance &&
                    Math.Abs(item.HeightTenthMm - expectedHeight) <= tolerance ||
                    Math.Abs(item.WidthTenthMm - expectedHeight) <= tolerance &&
                    Math.Abs(item.HeightTenthMm - expectedWidth) <= tolerance;
                var name = new string((item.Name ?? string.Empty)
                    .Where(char.IsLetterOrDigit)
                    .Select(char.ToUpperInvariant)
                    .ToArray());
                var namedFourBySix = name.Contains("4X6", StringComparison.Ordinal) &&
                    (name.Contains("PHOTO", StringComparison.Ordinal) ||
                     name == "4X6" ||
                     name.StartsWith("4X6", StringComparison.Ordinal));
                return exactDimensions || namedFourBySix;
            })
            .Select(item => item.Code)
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool IsRequestedPhotoMediaTypeAccepted(
        bool fieldReported,
        uint actualMediaType,
        short requestedMediaType) =>
        fieldReported && actualMediaType == unchecked((ushort)requestedMediaType);

    internal static bool IsRequestedOrientationAccepted(
        bool orientationFieldReported,
        short actualOrientation,
        short requestedOrientation) =>
        orientationFieldReported && actualOrientation == requestedOrientation;
}
