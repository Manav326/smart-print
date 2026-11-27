using System.Text.Json;

namespace SmartPrint.Api.Domain;

public sealed record PrinterDiagnosticSettings(
    string PaperSize,
    string ColourMode,
    bool IsDuplex,
    int PageCount,
    string CapabilityEvidence);

/// <summary>
/// Uses only positive, explicit Windows-driver capability evidence. Unknown is
/// never promoted to supported; the Windows executor remains the final authority.
/// </summary>
public static class PrinterDiagnosticRules
{
    private static readonly string[] PaperPreference = ["A4", "LETTER", "LEGAL", "A3", "A5"];

    public static bool TryResolveSettings(
        string? capabilitiesJson,
        out PrinterDiagnosticSettings settings,
        out string failureReason)
    {
        settings = new PrinterDiagnosticSettings("A4", "Mono", false, 1, "CAPABILITIES_UNKNOWN");
        failureReason = string.Empty;

        JsonDocument? parsed = null;
        try
        {
            if (string.IsNullOrWhiteSpace(capabilitiesJson))
                return true;

            parsed = JsonDocument.Parse(capabilitiesJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                failureReason = "The installed driver returned an invalid capability document.";
                return false;
            }

            var root = parsed.RootElement;
            var paperStatus = ReadString(root, "paperSizesStatus");
            var paperSize = "A4";
            var paperEvidence = "A4_FALLBACK_DRIVER_VALIDATION_REQUIRED";
            if (string.Equals(paperStatus, "supported", StringComparison.OrdinalIgnoreCase))
            {
                var names = ReadPaperNames(root);
                var supportedPaper = PaperPreference.FirstOrDefault(candidate =>
                    names.Any(name => IsPaperAlias(name, candidate)));
                if (supportedPaper is null)
                {
                    failureReason = "The driver reports paper sizes, but none of the standard diagnostic media sizes (A4, A3, A5, Letter or Legal) can be confirmed.";
                    return false;
                }
                paperSize = supportedPaper;
                paperEvidence = "WINDOWS_DRIVER_REPORTED";
            }

            var colorSupported = ReadExplicitBoolean(root, "color", "colorStatus");
            var duplexSupported = ReadExplicitBoolean(root, "duplex", "duplexStatus");
            var evidence = paperEvidence +
                (colorSupported ? ";COLOUR_SUPPORTED" : ";COLOUR_NOT_CONFIRMED") +
                (duplexSupported ? ";DUPLEX_SUPPORTED" : ";DUPLEX_NOT_CONFIRMED");
            settings = new PrinterDiagnosticSettings(
                paperSize,
                colorSupported ? "Colour" : "Mono",
                duplexSupported,
                duplexSupported ? 2 : 1,
                evidence);
            return true;
        }
        catch (JsonException)
        {
            failureReason = "The installed driver capability snapshot is malformed. Refresh printer discovery and retry.";
            return false;
        }
        finally
        {
            parsed?.Dispose();
        }
    }

    public static bool MatchesResolvedSettings(
        PrinterDiagnosticSettings settings,
        string? paperSize,
        string? colourMode,
        bool duplex,
        int pageCount) =>
        string.Equals(settings.PaperSize, paperSize, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(settings.ColourMode, colourMode, StringComparison.OrdinalIgnoreCase) &&
        settings.IsDuplex == duplex &&
        settings.PageCount == pageCount;

    private static bool ReadExplicitBoolean(JsonElement root, string valueName, string statusName) =>
        root.TryGetProperty(valueName, out var value) &&
        value.ValueKind == JsonValueKind.True &&
        string.Equals(ReadString(root, statusName), "supported", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadPaperNames(JsonElement root)
    {
        if (!root.TryGetProperty("paperSizes", out var values) || values.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return values.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => ReadString(item, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .ToArray();
    }

    private static bool IsPaperAlias(string name, string paper) =>
        paper switch
        {
            "A4" => name.Equals("A4", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("iso_a4", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("210 x 297", StringComparison.OrdinalIgnoreCase),
            "A3" => name.Equals("A3", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("iso_a3", StringComparison.OrdinalIgnoreCase),
            "A5" => name.Equals("A5", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("iso_a5", StringComparison.OrdinalIgnoreCase),
            "LETTER" => name.Equals("Letter", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("US Letter", StringComparison.OrdinalIgnoreCase),
            "LEGAL" => name.Equals("Legal", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("US Legal", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
}
