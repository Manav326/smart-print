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
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            normalized = document.RootElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
