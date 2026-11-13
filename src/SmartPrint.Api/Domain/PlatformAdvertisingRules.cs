using System.Net;
namespace SmartPrint.Api.Domain;
public static class PlatformAdvertisingRules
{
    public const long MaximumCreativeBytes = 3 * 1024 * 1024;
    public static bool IsSupportedPlacement(string? value) => value is "HOME" or "FILES";
    public static bool IsValidPlacementFormat(string? placement, string? format) =>
        (placement, format) is ("HOME", "BANNER") or ("FILES", "CARD");
    public static bool IsSafeDestinationUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback)
            return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (IPAddress.TryParse(uri.Host, out var address))
        {
            var bytes = address.GetAddressBytes();
            var privateV4 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                (bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 0 ||
                 (bytes[0] == 169 && bytes[1] == 254) ||
                 (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                 (bytes[0] == 192 && bytes[1] == 168));
            var uniqueLocalV6 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                (bytes[0] & 0xFE) == 0xFC;
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || privateV4 || uniqueLocalV6) return false;
        }
        return uri.Port is 443 or -1;
    }
    public static bool IsActiveNow(PlatformAdCampaign item, DateTimeOffset now) =>
        item.Status == "Active" && item.StartsUtc <= now && (item.EndsUtc is null || item.EndsUtc > now);
    public static string DisplayStatus(PlatformAdCampaign item, DateTimeOffset now)
    {
        if (item.Status == "Active" && item.EndsUtc is not null && item.EndsUtc <= now) return "Expired";
        if (item.Status == "Active" && item.StartsUtc > now) return "Scheduled";
        return item.Status;
    }
}
