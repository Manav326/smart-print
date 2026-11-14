namespace SmartPrint.Api.Domain;

public static class ShopStationRules
{
    public static bool TryNormalizePublicPortalBaseUrl(string? supplied, out string normalized)
    {
        normalized = (supplied ?? string.Empty).TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    public static bool TryNormalizeDisplayName(string? supplied, out string displayName)
    {
        displayName = supplied?.Trim() ?? string.Empty;
        return displayName.Length is > 0 and <= 120;
    }

    public static bool IsSupportedOutputMode(string? outputMode) =>
        outputMode is "PHYSICAL" or "PDF_EXPORT";
}
