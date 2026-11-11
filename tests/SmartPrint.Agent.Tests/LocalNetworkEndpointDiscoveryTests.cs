using System.Net;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class LocalNetworkEndpointDiscoveryTests
{
    [Fact]
    public void UsesSavedNetworkWhenItIsAvailable()
    {
        var networks = new[]
        {
            new LocalNetworkAdapter("wifi|ShopWifi", "ShopWifi", "Wi-Fi", "192.168.31.47", 24),
            new LocalNetworkAdapter("ethernet", "Ethernet", "Ethernet", "10.0.0.12", 16)
        };

        var selected = LocalNetworkEndpointDiscovery.SelectNetwork(networks, "ethernet");

        Assert.NotNull(selected);
        Assert.Equal("ethernet", selected!.Id);
    }

    [Fact]
    public void DoesNotSilentlySwitchWhenSavedNetworkIsUnavailable()
    {
        var networks = new[]
        {
            new LocalNetworkAdapter("wifi|Guest", "Guest", "Wi-Fi", "192.168.31.47", 24),
            new LocalNetworkAdapter("ethernet", "Ethernet", "Ethernet", "10.0.0.12", 16)
        };

        Assert.Null(LocalNetworkEndpointDiscovery.SelectNetwork(networks, "wifi|ShopWifi"));
    }

    [Fact]
    public void RequiresOwnerSelectionWhenSavedWifiChangesToPhoneHotspotOnSameAdapter()
    {
        const string adapterId = "6f9d30d2-5e52-4a73-aef1-07c0e8a72e11";
        const string savedRouterId = adapterId + "|SSID:53686F70526F75746572";
        const string phoneHotspotId = adapterId + "|SSID:50686F6E65486F7473706F74";
        var phoneHotspot = new LocalNetworkAdapter(
            phoneHotspotId, "Phone hotspot", "Wi-Fi", "192.168.43.27", 24);

        // A reused Wi-Fi adapter must not silently replace the owner's saved SSID.
        Assert.Null(LocalNetworkEndpointDiscovery.SelectNetwork([phoneHotspot], savedRouterId));

        // The owner can explicitly select the newly connected hotspot.
        Assert.Equal(phoneHotspotId,
            LocalNetworkEndpointDiscovery.SelectNetwork([phoneHotspot], phoneHotspotId)!.Id);
    }

    [Fact]
    public void ChoosesWifiOnlyWhenThereIsNoSavedPreference()
    {
        var networks = new[]
        {
            new LocalNetworkAdapter("ethernet", "Ethernet", "Ethernet", "10.0.0.12", 16),
            new LocalNetworkAdapter("wifi|ShopWifi", "ShopWifi", "Wi-Fi", "192.168.31.47", 24)
        };

        Assert.Equal("wifi|ShopWifi", LocalNetworkEndpointDiscovery.SelectNetwork(networks, null)!.Id);
    }

    [Fact]
    public void KeepsWifiSsidCaseDifferencesAsDifferentSelections()
    {
        var shopWifi = new LocalNetworkAdapter("wifi|53686F7057694669", "ShopWifi", "Wi-Fi", "192.168.31.47", 24);
        var shopWifiLowerCase = new LocalNetworkAdapter("wifi|73686F7077696669", "shopwifi", "Wi-Fi", "192.168.31.47", 24);

        Assert.Equal(shopWifi.Id, LocalNetworkEndpointDiscovery.SelectNetwork([shopWifi, shopWifiLowerCase], shopWifi.Id)!.Id);
        Assert.Equal(shopWifiLowerCase.Id, LocalNetworkEndpointDiscovery.SelectNetwork([shopWifi, shopWifiLowerCase], shopWifiLowerCase.Id)!.Id);
    }

    [Theory]
    [InlineData("192.168.31.47", 24, true)]
    [InlineData("10.0.0.12", 16, true)]
    [InlineData("192.168.43.27", 24, true)]
    [InlineData("172.20.10.2", 28, true)]
    [InlineData("169.254.1.8", 16, false)]
    [InlineData("8.8.8.8", 24, false)]
    [InlineData("192.168.31.47", 0, false)]
    [InlineData("192.168.31.47", 33, false)]
    public void AcceptsPrivateShopAddressesWithoutRequiringAnInternetGateway(
        string address, int prefixLength, bool expected)
    {
        Assert.Equal(
            expected,
            LocalNetworkEndpointDiscovery.IsUsableShopAddress(IPAddress.Parse(address), prefixLength));
    }

    [Fact]
    public void RejectsRequestsThatArriveOnAnotherLocalAdapter()
    {
        var remotePhone = IPAddress.Parse("192.168.31.99");
        var selectedShopIp = IPAddress.Parse("192.168.31.47");
        var otherAdapterIp = IPAddress.Parse("10.0.0.12");

        Assert.True(LocalNetworkEndpointDiscovery.IsConnectionOnSelectedInterface(
            remotePhone, selectedShopIp, selectedShopIp, 24));
        Assert.False(LocalNetworkEndpointDiscovery.IsConnectionOnSelectedInterface(
            remotePhone, otherAdapterIp, selectedShopIp, 24));
    }

    [Theory]
    [InlineData("192.168.31.99", "192.168.31.47", 24, true)]
    [InlineData("192.168.32.99", "192.168.31.47", 24, false)]
    [InlineData("10.10.4.12", "10.10.0.12", 16, true)]
    [InlineData("10.11.4.12", "10.10.0.12", 16, false)]
    [InlineData("192.168.31.99", "192.168.31.47", 32, false)]
    [InlineData("192.168.31.47", "192.168.31.47", 32, true)]
    public void MatchesOnlyTheSelectedIpv4Subnet(string remote, string local, int prefixLength, bool expected)
    {
        Assert.Equal(
            expected,
            LocalNetworkEndpointDiscovery.IsAddressOnNetwork(
                IPAddress.Parse(remote),
                IPAddress.Parse(local),
                prefixLength));
    }
}
