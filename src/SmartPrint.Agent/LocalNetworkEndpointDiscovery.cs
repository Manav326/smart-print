using System.Net;
using System.Text;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SmartPrint.Agent;

public sealed record LocalNetworkAdapter(
    string Id,
    string DisplayName,
    string Kind,
    string Ipv4Address,
    int PrefixLength);

public static class LocalNetworkEndpointDiscovery
{
    public const int DefaultPort = 17842;

    public static IReadOnlyList<LocalNetworkAdapter> GetAvailableNetworks()
    {
        var networks = new List<LocalNetworkAdapter>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            try
            {
                var properties = network.GetIPProperties();

                // Same-subnet phone-to-PC communication does not need an Internet
                // gateway. Include isolated shop Wi-Fi/LANs with static private
                // addresses; the one-time phone test proves actual reachability.
                var addressInfo = properties.UnicastAddresses
                    .Where(unicast => IsUsableShopAddress(unicast.Address, unicast.PrefixLength))
                    .OrderBy(unicast => unicast.Address.ToString(), StringComparer.Ordinal)
                    .FirstOrDefault();
                if (addressInfo is null) continue;

                var kind = network.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet => "Ethernet",
                    _ => "Network"
                };
                var ssid = kind == "Wi-Fi" ? WindowsWifiNetworkName.TryGetCurrentSsid(network.Id) : null;
                var displayName = !string.IsNullOrWhiteSpace(ssid)
                    ? ssid.Trim()
                    : string.IsNullOrWhiteSpace(network.Name)
                        ? kind + " network"
                        : network.Name.Trim();

                // Include the active SSID in the identity. If it changes, the saved
                // shop connection no longer matches and the owner must explicitly select
                // the newly connected network from the desktop Shop Connection panel.
                var selectionId = kind == "Wi-Fi"
                    ? ssid is null
                        ? $"{network.Id}|SSID-UNAVAILABLE"
                        : $"{network.Id}|SSID:{Convert.ToHexString(Encoding.UTF8.GetBytes(ssid))}"
                    : network.Id;

                networks.Add(new LocalNetworkAdapter(
                    selectionId,
                    displayName,
                    kind,
                    addressInfo.Address.ToString(),
                    addressInfo.PrefixLength));
            }
            catch (NetworkInformationException)
            {
                // An adapter may disappear while Windows is enumerating interfaces.
            }
        }

        return networks
            .OrderBy(network => NetworkPriority(network.Kind))
            .ThenBy(network => network.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(network => network.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static LocalNetworkAdapter? SelectNetwork(
        IEnumerable<LocalNetworkAdapter> availableNetworks,
        string? preferredNetworkId)
    {
        var networks = availableNetworks.ToArray();
        if (!string.IsNullOrWhiteSpace(preferredNetworkId))
        {
            // Do not silently move a shop from its saved Wi-Fi to a guest, hotspot
            // or other adapter when the selected network disappears. The current
            // connection remains available in the owner-selectable network list.
            return networks.FirstOrDefault(network =>
                string.Equals(network.Id, preferredNetworkId, StringComparison.OrdinalIgnoreCase));
        }

        return networks
            .OrderBy(network => NetworkPriority(network.Kind))
            .ThenBy(network => network.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(network => network.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static IReadOnlyList<string> Discover(int port = DefaultPort) =>
        DiscoverForNetwork(null, port);

    public static IReadOnlyList<string> DiscoverForNetwork(string? preferredNetworkId, int port = DefaultPort)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var selected = SelectNetwork(GetAvailableNetworks(), preferredNetworkId);
        return selected is null ? Array.Empty<string>() : [$"http://{selected.Ipv4Address}:{port}"];
    }

    public static bool IsUsableShopAddress(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);
        return prefixLength is >= 1 and <= 32 && IsPrivateIpv4(address);
    }

    public static bool IsAddressOnNetwork(IPAddress remoteAddress, IPAddress localAddress, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(localAddress);

        if (remoteAddress.IsIPv4MappedToIPv6) remoteAddress = remoteAddress.MapToIPv4();
        if (remoteAddress.AddressFamily != AddressFamily.InterNetwork ||
            localAddress.AddressFamily != AddressFamily.InterNetwork ||
            prefixLength is < 0 or > 32)
            return false;

        var remote = remoteAddress.GetAddressBytes();
        var local = localAddress.GetAddressBytes();
        for (var index = 0; index < 4; index++)
        {
            var bits = Math.Clamp(prefixLength - index * 8, 0, 8);
            var mask = bits == 0 ? 0 : (byte)(0xff << (8 - bits));
            if ((remote[index] & mask) != (local[index] & mask)) return false;
        }

        return true;
    }

    public static bool IsConnectionOnSelectedInterface(
        IPAddress remoteAddress,
        IPAddress localEndpointAddress,
        IPAddress selectedAddress,
        int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(localEndpointAddress);
        ArgumentNullException.ThrowIfNull(selectedAddress);

        if (localEndpointAddress.IsIPv4MappedToIPv6)
            localEndpointAddress = localEndpointAddress.MapToIPv4();
        if (selectedAddress.IsIPv4MappedToIPv6)
            selectedAddress = selectedAddress.MapToIPv4();

        return localEndpointAddress.Equals(selectedAddress) &&
            IsAddressOnNetwork(remoteAddress, selectedAddress, prefixLength);
    }

    private static int NetworkPriority(string kind) => kind switch
    {
        "Wi-Fi" => 0,
        "Ethernet" => 1,
        _ => 2
    };

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
