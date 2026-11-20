using System.Text.Json;
using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Modules.ShopDevices.Domain;

/// <summary>
/// Pure validation and normalization for shop printer and QR-station inputs.
/// </summary>
public static class ShopDeviceRules
{
    private static readonly string[] ConnectionTypes = ["USB", "LAN", "IPP", "WindowsQueue"];

    public static Dictionary<string, string[]> ValidatePrinter(CreatePrinterRequest request, string capabilities)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 160 ||
            string.IsNullOrWhiteSpace(request.Manufacturer) || request.Manufacturer.Trim().Length > 80 ||
            string.IsNullOrWhiteSpace(request.Model) || request.Model.Trim().Length > 120 ||
            !ConnectionTypes.Contains(request.ConnectionType, StringComparer.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string[]>
            {
                ["printer"] = ["A name, manufacturer, model and supported connection type are required."]
            };
        }

        try
        {
            using var parsed = JsonDocument.Parse(capabilities);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object || capabilities.Length > 16000)
                throw new JsonException();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string[]>
            {
                ["capabilitiesJson"] = ["Printer capabilities must be a valid JSON object under 16 KB."]
            };
        }

        return new Dictionary<string, string[]>();
    }

    public static string NormalizeConnectionType(string value) =>
        ConnectionTypes.Single(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));

    public static Dictionary<string, string[]> ValidateQrStationName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 120)
            return new Dictionary<string, string[]>
            {
                ["displayName"] = ["A station name of up to 120 characters is required."]
            };

        return new Dictionary<string, string[]>();
    }

    public static bool IsCanonicalPublicPortalBase(string? value)
    {
        var portalBase = (value ?? string.Empty).TrimEnd('/');
        return Uri.TryCreate(portalBase, UriKind.Absolute, out var portalUri) &&
            portalUri.Scheme == Uri.UriSchemeHttps &&
            portalUri.AbsolutePath == "/" &&
            string.IsNullOrEmpty(portalUri.Query) &&
            string.IsNullOrEmpty(portalUri.Fragment) &&
            string.IsNullOrEmpty(portalUri.UserInfo);
    }

    public static string NormalizePublicPortalBase(string value) => value.TrimEnd('/');
}
