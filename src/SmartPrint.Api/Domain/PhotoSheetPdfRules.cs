using System.Text.Json;

namespace SmartPrint.Api.Domain;

public static class PhotoSheetPdfRules
{
    private const double A4WidthPoints = 210d * 72d / 25.4d;
    private const double A4HeightPoints = 297d * 72d / 25.4d;
    private const double MediaBoxTolerancePoints = 1.5d;

    private static bool HasFullPagePhotoRaster(
        JsonElement page,
        int expectedPixelWidth,
        int expectedPixelHeight,
        double expectedWidthPoints,
        double expectedHeightPoints)
    {
        if (!page.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Array ||
            images.GetArrayLength() != 1)
            return false;

        var image = images[0];
        if (image.ValueKind != JsonValueKind.Object ||
            !TryGetNumber(image, "widthInSamples", out var pixelWidth) ||
            !TryGetNumber(image, "heightInSamples", out var pixelHeight) ||
            !TryGetNumber(image, "leftPoints", out var left) ||
            !TryGetNumber(image, "bottomPoints", out var bottom) ||
            !TryGetNumber(image, "rightPoints", out var right) ||
            !TryGetNumber(image, "topPoints", out var top) ||
            !TryGetNumber(image, "rotationDegrees", out var rotation) ||
            !image.TryGetProperty("isImageMask", out var maskElement) ||
            maskElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;

        return pixelWidth == expectedPixelWidth &&
            pixelHeight == expectedPixelHeight &&
            !maskElement.GetBoolean() &&
            Math.Abs(rotation) <= 0.01 &&
            Math.Abs(left) <= MediaBoxTolerancePoints &&
            Math.Abs(bottom) <= MediaBoxTolerancePoints &&
            Math.Abs(right - expectedWidthPoints) <= MediaBoxTolerancePoints &&
            Math.Abs(top - expectedHeightPoints) <= MediaBoxTolerancePoints;
    }

    private static bool TryGetNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value);
    }

    private static bool HasExactlyPageGeometry(
        string pageSizesJson,
        int expectedPageCount,
        double expectedWidthPoints,
        double expectedHeightPoints,
        int expectedPixelWidth,
        int expectedPixelHeight)
    {
        if (expectedPageCount is < 1 or > 100 || string.IsNullOrWhiteSpace(pageSizesJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(pageSizesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != expectedPageCount)
                return false;

            foreach (var page in document.RootElement.EnumerateArray())
            {
                if (page.ValueKind != JsonValueKind.Object ||
                    !page.TryGetProperty("widthPoints", out var widthElement) ||
                    !page.TryGetProperty("heightPoints", out var heightElement) ||
                    widthElement.ValueKind != JsonValueKind.Number ||
                    heightElement.ValueKind != JsonValueKind.Number ||
                    !widthElement.TryGetDouble(out var width) ||
                    !heightElement.TryGetDouble(out var height) ||
                    Math.Abs(width - expectedWidthPoints) > MediaBoxTolerancePoints ||
                    Math.Abs(height - expectedHeightPoints) > MediaBoxTolerancePoints ||
                    !HasFullPagePhotoRaster(page, expectedPixelWidth, expectedPixelHeight, expectedWidthPoints, expectedHeightPoints))
                    return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool HasExactlyA4PortraitPages(string pageSizesJson, int expectedPageCount) =>
        HasExactlyPageGeometry(
            pageSizesJson,
            expectedPageCount,
            A4WidthPoints,
            A4HeightPoints,
            2480,
            3508);

    /// <summary>
    /// Validates either a full-page A4 photo sheet or one 300-DPI full-bleed
    /// physical photo print per PDF page, using the selected physical dimensions.
    /// </summary>
    public static bool HasExactlyPhotoPages(
        string pageSizesJson,
        int expectedPageCount,
        string? outputLayout,
        string? photoSizeCode)
    {
        if (string.Equals(outputLayout, "A4_SHEET", StringComparison.OrdinalIgnoreCase))
            return HasExactlyA4PortraitPages(pageSizesJson, expectedPageCount);

        if (!string.Equals(outputLayout, "PHOTO_PAPER", StringComparison.OrdinalIgnoreCase) ||
            !TryGetPhotoSize(photoSizeCode, out var widthMm, out var heightMm))
            return false;

        var widthPoints = widthMm * 72d / 25.4d;
        var heightPoints = heightMm * 72d / 25.4d;
        var pixelWidth = (int)Math.Round(widthMm / 25.4d * 300d, MidpointRounding.AwayFromZero);
        var pixelHeight = (int)Math.Round(heightMm / 25.4d * 300d, MidpointRounding.AwayFromZero);
        return HasExactlyPageGeometry(
            pageSizesJson,
            expectedPageCount,
            widthPoints,
            heightPoints,
            pixelWidth,
            pixelHeight);
    }

    public static bool IsValidPhotoOutputOptions(string? outputLayout, string? paperFinish, int photosPerPage)
    {
        if (string.Equals(outputLayout, "A4_SHEET", StringComparison.OrdinalIgnoreCase))
            return string.Equals(paperFinish, "PLAIN", StringComparison.OrdinalIgnoreCase) && photosPerPage >= 1;
        if (string.Equals(outputLayout, "PHOTO_PAPER", StringComparison.OrdinalIgnoreCase))
            return paperFinish is not null &&
                (string.Equals(paperFinish, "GLOSSY", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(paperFinish, "MATTE", StringComparison.OrdinalIgnoreCase)) &&
                photosPerPage == 1;
        return false;
    }

    public static bool TryGetPhotoSize(string? photoSizeCode, out double widthMm, out double heightMm)
    {
        (double Width, double Height)? dimensions = photoSizeCode?.Trim().ToLowerInvariant() switch
        {
            "35x45" => (35d, 45d),
            "25x35" => (25d, 35d),
            "2x2" => (50.8d, 50.8d),
            "4x6" => (101.6d, 152.4d),
            _ => null
        };
        widthMm = dimensions?.Width ?? 0;
        heightMm = dimensions?.Height ?? 0;
        return dimensions.HasValue;
    }
}
