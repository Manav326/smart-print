using System.Text.Json;

namespace SmartPrint.Api.Domain;

public static class PrinterPagesPerSheetRules
{
    public static bool Supports(string? capabilitiesJson, int requestedPagesPerSheet)
    {
        if (requestedPagesPerSheet == 1)
            return true;
        if (requestedPagesPerSheet is < 2 or > 500 || string.IsNullOrWhiteSpace(capabilitiesJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(capabilitiesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var direction = document.RootElement.TryGetProperty("pagesPerSheetDirection", out var directionValue) &&
                            directionValue.ValueKind == JsonValueKind.String
                ? directionValue.GetString()
                : null;
            if (direction is not ("RightBottom" or "BottomRight" or "LeftBottom" or "BottomLeft" or
                "RightTop" or "TopRight" or "LeftTop" or "TopLeft"))
                return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "pagesPerSheet", StringComparison.OrdinalIgnoreCase) ||
                    property.Value.ValueKind != JsonValueKind.Array)
                    continue;

                return property.Value.EnumerateArray().Any(value =>
                    value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt32(out var supported) &&
                    supported == requestedPagesPerSheet);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
