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

    // Prefer exact/standard photo finish names over vendor-specific variants.
    // Semi-gloss, satin and lustre/luster are intentionally not silently folded
    // into glossy or matte because their physical finish is different.
    internal static short? SelectPhotoMediaTypeCode(
        IReadOnlyList<(short Code, string Name)> mediaTypes,
        string finish)
    {
        var requested = finish.Trim().ToUpperInvariant();
        if (mediaTypes.Count == 0 || requested is not ("GLOSSY" or "MATTE"))
            return null;

        var matches = mediaTypes
            .Select(item => (item.Code, Rank: GetLegacyPhotoMediaMatchRank(item.Name, requested)))
            .Where(item => item.Rank >= 0)
            .GroupBy(item => item.Rank)
            .OrderBy(group => group.Key)
            .FirstOrDefault();
        if (matches is null)
            return null;

        var codes = matches.Select(item => item.Code).Distinct().ToArray();
        return codes.Length == 1 ? codes[0] : null;
    }

    private static int GetLegacyPhotoMediaMatchRank(string? value, string finish)
    {
        var name = new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (name.Length == 0 ||
            name.Contains("SEMIGLOSS", StringComparison.Ordinal) ||
            name.Contains("SATIN", StringComparison.Ordinal) ||
            name.Contains("LUSTRE", StringComparison.Ordinal) ||
            name.Contains("LUSTER", StringComparison.Ordinal))
            return -1;

        if (finish == "MATTE")
        {
            if (name.Contains("PHOTOGRAPHICMATTE", StringComparison.Ordinal) ||
                name.Contains("MATTEPHOTO", StringComparison.Ordinal))
                return 0;
            if (name.Contains("MATTE", StringComparison.Ordinal) ||
                name.Contains("MATT", StringComparison.Ordinal))
                return 1;
            return -1;
        }

        if (name.Contains("PHOTOGRAPHICGLOSSY", StringComparison.Ordinal) ||
            name.Contains("GLOSSYPHOTO", StringComparison.Ordinal))
            return 0;
        if (name.Contains("GLOSSY", StringComparison.Ordinal))
            return 1;
        if (name.Contains("HIGHGLOSS", StringComparison.Ordinal))
            return 2;
        if (name.Contains("GLOSS", StringComparison.Ordinal))
            return 3;
        return -1;
    }

    // Print Schema values are canonical enum names, not localized driver labels.
    // Only exact glossy/high-gloss and matte categories satisfy these two finishes.
    internal static string? SelectPhotoPrintSchemaMediaType(
        IEnumerable<string> supportedMediaTypes,
        string finish)
    {
        ArgumentNullException.ThrowIfNull(supportedMediaTypes);
        var available = new HashSet<string>(
            supportedMediaTypes.Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.Ordinal);

        var preference = finish.Trim().ToUpperInvariant() switch
        {
            "GLOSSY" => new[] { "PhotographicGlossy", "PhotographicHighGloss" },
            "MATTE" => new[] { "PhotographicMatte" },
            _ => Array.Empty<string>()
        };
        return preference.FirstOrDefault(available.Contains);
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
