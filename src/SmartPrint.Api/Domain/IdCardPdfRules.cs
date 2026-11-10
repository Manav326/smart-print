using System.Text.Json;

namespace SmartPrint.Api.Domain;

public static class IdCardPdfRules
{
    private const double A4WidthPoints = 210d * 72d / 25.4d;
    private const double A4HeightPoints = 297d * 72d / 25.4d;
    private const double MediaBoxTolerancePoints = 1.5d;

    /// <summary>
    /// Validates the PDF media box recorded by DocumentAnalyzer. ID card sheets
    /// must be one portrait A4 page so the Agent can preserve the physical card
    /// dimensions instead of applying the normal fit-to-printable-area policy.
    /// </summary>
    public static bool HasExactlyA4PortraitPage(string pageSizesJson)
    {
        if (string.IsNullOrWhiteSpace(pageSizesJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(pageSizesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != 1)
                return false;

            var page = document.RootElement[0];
            if (page.ValueKind != JsonValueKind.Object ||
                !TryGetNumber(page, "widthPoints", out var width) ||
                !TryGetNumber(page, "heightPoints", out var height))
                return false;

            return width > 0 &&
                height > width &&
                Math.Abs(width - A4WidthPoints) <= MediaBoxTolerancePoints &&
                Math.Abs(height - A4HeightPoints) <= MediaBoxTolerancePoints;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value);
    }
}
