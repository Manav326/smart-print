using System.Net;
using System.Net.Sockets;

namespace SmartPrint.Api.Domain;

public static class AgentLocalEndpointRules
{
    public const int LocalApiPort = 17842;
    private const int MaximumEndpoints = 8;

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? endpoints)
    {
        if (endpoints is null) return Array.Empty<string>();

        var normalized = new List<string>();
        foreach (var value in endpoints.Take(64))
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp ||
                uri.Port != LocalApiPort ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                uri.AbsolutePath is not ("" or "/") ||
                !IPAddress.TryParse(uri.Host, out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                !IsPrivateIpv4(address))
            {
                continue;
            }

            var endpoint = $"http://{address}:{LocalApiPort}";
            if (!normalized.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(endpoint);
            }

            if (normalized.Count == MaximumEndpoints) break;
        }

        return normalized;
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
