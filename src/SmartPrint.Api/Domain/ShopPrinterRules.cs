using System.Text.Json;
using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public enum ShopPrinterValidationFailure
{
    None,
    InvalidPrinter,
    InvalidCapabilities
}

public static class ShopPrinterRules
{
    private static readonly string[] ConnectionTypes = ["USB", "LAN", "IPP", "WindowsQueue"];

    public static ShopPrinterValidationFailure Validate(
        CreatePrinterRequest request,
        out string capabilities,
        out string normalizedConnectionType)
    {
        ArgumentNullException.ThrowIfNull(request);
        capabilities = request.CapabilitiesJson ?? "{}";
        normalizedConnectionType = string.Empty;

        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 160 ||
            string.IsNullOrWhiteSpace(request.Manufacturer) || request.Manufacturer.Trim().Length > 80 ||
            string.IsNullOrWhiteSpace(request.Model) || request.Model.Trim().Length > 120 ||
            !ConnectionTypes.Contains(request.ConnectionType, StringComparer.OrdinalIgnoreCase))
        {
            return ShopPrinterValidationFailure.InvalidPrinter;
        }

        try
        {
            using var parsed = JsonDocument.Parse(capabilities);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object || capabilities.Length > 16000)
                throw new JsonException();
        }
        catch (JsonException)
        {
            return ShopPrinterValidationFailure.InvalidCapabilities;
        }

        normalizedConnectionType = ConnectionTypes.Single(value =>
            value.Equals(request.ConnectionType, StringComparison.OrdinalIgnoreCase));
        return ShopPrinterValidationFailure.None;
    }
}
