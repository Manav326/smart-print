using System.Net;

namespace SmartPrint.Agent;

public sealed record LocalNetworkSnapshot(
    IReadOnlyList<LocalNetworkAdapter> Networks,
    string? SelectedNetworkId,
    IReadOnlyList<string> Endpoints,
    DateTimeOffset? PhoneVerifiedUtc);

public sealed record LocalPhoneTest(string PhoneTestUrl, DateTimeOffset StartedUtc, DateTimeOffset ExpiresUtc);

public sealed class LocalNetworkManager(LocalNetworkPreferenceStore preferences, LocalAgentApiState state)
{
    private readonly SemaphoreSlim selectionChanged = new(0, 1);

    public LocalNetworkSnapshot GetSnapshot()
    {
        var networks = LocalNetworkEndpointDiscovery.GetAvailableNetworks();
        var preferredId = preferences.GetPreferredNetworkId();
        var selected = LocalNetworkEndpointDiscovery.SelectNetwork(networks, preferredId);

        // Persist only the first automatic choice. If the saved network disappears
        // (including a router-to-phone-hotspot SSID change), keep it unselected until
        // the owner explicitly chooses the currently connected network.
        if (string.IsNullOrWhiteSpace(preferredId) && selected is not null)
            preferences.SetPreferredNetworkId(selected.Id);

        var endpoints = selected is null
            ? Array.Empty<string>()
            : new[] { $"http://{selected.Ipv4Address}:{LocalNetworkEndpointDiscovery.DefaultPort}" };

        return new LocalNetworkSnapshot(
            networks,
            selected?.Id,
            endpoints,
            selected is null ? null : state.GetPhoneVerifiedUtc(GetPhoneVerificationKey(selected)));
    }

    public LocalNetworkSnapshot SelectNetwork(string networkId)
    {
        var networks = LocalNetworkEndpointDiscovery.GetAvailableNetworks();
        if (!networks.Any(network => string.Equals(network.Id, networkId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That connection is no longer available. Refresh the list and choose again.");

        var currentId = preferences.GetPreferredNetworkId();
        if (!string.Equals(currentId, networkId, StringComparison.OrdinalIgnoreCase))
        {
            preferences.SetPreferredNetworkId(networkId);
            state.ClearPhoneVerification();
            SignalSelectionChanged();
        }
        return GetSnapshot();
    }

    public bool IsRemoteClientOnSelectedNetwork(IPAddress? remoteAddress, IPAddress? localAddress)
    {
        if (remoteAddress is null || localAddress is null) return false;
        if (IPAddress.IsLoopback(remoteAddress))
            return IPAddress.IsLoopback(localAddress);

        if (remoteAddress.IsIPv4MappedToIPv6) remoteAddress = remoteAddress.MapToIPv4();
        if (localAddress.IsIPv4MappedToIPv6) localAddress = localAddress.MapToIPv4();
        if (remoteAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var snapshot = GetSnapshot();
        var selected = snapshot.Networks.FirstOrDefault(network =>
            string.Equals(network.Id, snapshot.SelectedNetworkId, StringComparison.OrdinalIgnoreCase));
        return selected is not null &&
            IPAddress.TryParse(selected.Ipv4Address, out var selectedAddress) &&
            LocalNetworkEndpointDiscovery.IsConnectionOnSelectedInterface(
                remoteAddress, localAddress, selectedAddress, selected.PrefixLength);
    }

    public Task<bool> WaitForSelectionChangeOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        selectionChanged.WaitAsync(delay, cancellationToken);

    private void SignalSelectionChanged()
    {
        if (selectionChanged.CurrentCount == 0)
        {
            try { selectionChanged.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    public LocalPhoneTest StartPhoneTest()
    {
        if (!state.IsRunning)
            throw new InvalidOperationException("SmartPrint is still starting. Try again in a moment.");

        var snapshot = GetSnapshot();
        var endpoint = snapshot.Endpoints.FirstOrDefault();
        if (snapshot.SelectedNetworkId is null || endpoint is null)
            throw new InvalidOperationException("Connect this computer to the shop Wi-Fi or network, then try again.");

        var selected = snapshot.Networks.First(network => network.Id == snapshot.SelectedNetworkId);
        var (token, expiresUtc) = state.CreatePhoneProbe(GetPhoneVerificationKey(selected));
        var startedUtc = DateTimeOffset.UtcNow;
        var url = $"{endpoint}/api/v1/local/connection-check?token={Uri.EscapeDataString(token)}";
        return new LocalPhoneTest(url, startedUtc, expiresUtc);
    }

    private static string GetPhoneVerificationKey(LocalNetworkAdapter network) =>
        $"{network.Id}|{network.Ipv4Address}/{network.PrefixLength}";
}
