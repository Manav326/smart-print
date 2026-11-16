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

    internal static bool TryGetPhotoPaperDimensions(
        string sizeCode,
        out short widthTenthMm,
        out short heightTenthMm)
    {
        (short Width, short Height)? dimensions = sizeCode.Trim().ToLowerInvariant() switch
        {
            "35x45" => (350, 450),
            "25x35" => (250, 350),
            "2x2" => (508, 508),
            "4x6" => (1016, 1524),
            _ => null
        };
        widthTenthMm = dimensions?.Width ?? 0;
        heightTenthMm = dimensions?.Height ?? 0;
        return dimensions.HasValue;
    }

    internal static bool IsRequestedPhotoPaperSizeAccepted(
        bool paperSizeFieldReported,
        bool paperWidthFieldReported,
        bool paperLengthFieldReported,
        short actualPaperSize,
        short actualWidthTenthMm,
        short actualHeightTenthMm,
        short expectedWidthTenthMm,
        short expectedHeightTenthMm) =>
        paperSizeFieldReported && paperWidthFieldReported && paperLengthFieldReported &&
        actualPaperSize == 0 &&
        actualWidthTenthMm == expectedWidthTenthMm &&
        actualHeightTenthMm == expectedHeightTenthMm;

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
