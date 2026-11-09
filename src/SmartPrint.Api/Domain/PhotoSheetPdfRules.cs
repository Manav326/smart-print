using System.Text.Json;

namespace SmartPrint.Api.Domain;

public static class PhotoSheetPdfRules
{
    private const double A4WidthPoints = 210d * 72d / 25.4d;
    private const double A4HeightPoints = 297d * 72d / 25.4d;
    private const double MediaBoxTolerancePoints = 1.5d;

    private static bool HasFullPagePhotoRaster(JsonElement page)
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

        return pixelWidth == 2480 &&
            pixelHeight == 3508 &&
            !maskElement.GetBoolean() &&
            Math.Abs(rotation) <= 0.01 &&
            Math.Abs(left) <= MediaBoxTolerancePoints &&
            Math.Abs(bottom) <= MediaBoxTolerancePoints &&
            Math.Abs(right - A4WidthPoints) <= MediaBoxTolerancePoints &&
            Math.Abs(top - A4HeightPoints) <= MediaBoxTolerancePoints;
    }

    private static bool TryGetNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value);
    }

    public static bool HasExactlyA4PortraitPages(string pageSizesJson, int expectedPageCount)
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
                    Math.Abs(width - A4WidthPoints) > MediaBoxTolerancePoints ||
                    Math.Abs(height - A4HeightPoints) > MediaBoxTolerancePoints ||
                    !HasFullPagePhotoRaster(page))
                    return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
