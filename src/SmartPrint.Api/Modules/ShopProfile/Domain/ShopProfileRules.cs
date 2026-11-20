namespace SmartPrint.Api.Modules.ShopProfile.Domain;

/// <summary>
/// Pure validation for shop display name, accent colour and optional logo URL.
/// </summary>
public static class ShopProfileRules
{
    public static Dictionary<string, string[]> Validate(string displayName, string accentColor, string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 180 ||
            !IsHexColor(accentColor) ||
            (logoUrl is not null &&
             (!Uri.TryCreate(logoUrl, UriKind.Absolute, out var logoUri) ||
              logoUri.Scheme != Uri.UriSchemeHttps)))
        {
            return new Dictionary<string, string[]>
            {
                ["profile"] = ["Provide a display name, a six-digit hex accent colour, and an optional HTTPS logo URL."]
            };
        }

        return new Dictionary<string, string[]>();
    }

    public static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
}
