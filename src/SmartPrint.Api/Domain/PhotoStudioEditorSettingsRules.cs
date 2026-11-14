using System.Text.Json;

namespace SmartPrint.Api.Domain;

public static class PhotoStudioEditorSettingsRules
{
    public const int MaximumJsonLength = 16 * 1024;

    public static bool TryNormalizeJson(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (value.Length > MaximumJsonLength)
            return false;

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                MaxDepth = 12
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            // These values are hydrated directly by the Photo Studio UI. Validate
            // their structural types at the API boundary so a malformed saved object
            // cannot crash the editor when a shop reopens a photo revision.
            if (root.TryGetProperty("photoName", out var photoName) &&
                photoName.ValueKind != JsonValueKind.String)
                return false;
            if (root.TryGetProperty("dateOfBirth", out var dateOfBirth) &&
                dateOfBirth.ValueKind != JsonValueKind.String)
                return false;
            if (root.TryGetProperty("adjustments", out var adjustments) &&
                adjustments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return false;
            if (root.TryGetProperty("textLayers", out var textLayers))
            {
                if (textLayers.ValueKind != JsonValueKind.Array || textLayers.GetArrayLength() > 20)
                    return false;
                if (textLayers.EnumerateArray().Any(layer => layer.ValueKind != JsonValueKind.Object))
                    return false;
            }

            normalized = root.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
