using SmartPrint.Api.Modules.ShopDevices.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopAgentDeviceRulesTests
{
    [Fact]
    public void ReadsAdvertisedLocalAgentEndpoints()
    {
        var endpoints = ShopAgentDeviceRules.ReadLocalAgentEndpoints(
            "[\"http://127.0.0.1:17842\",\"http://192.168.1.20:17842\"]");

        Assert.Equal(
            ["http://127.0.0.1:17842", "http://192.168.1.20:17842"],
            endpoints);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("null")]
    public void MalformedOrUnexpectedEndpointPayloadFailsClosed(string? json)
    {
        Assert.Empty(ShopAgentDeviceRules.ReadLocalAgentEndpoints(json));
    }
}
