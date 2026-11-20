using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.ShopDevices.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopDeviceRulesTests
{
    [Theory]
    [InlineData("USB")]
    [InlineData("lan")]
    [InlineData("IPP")]
    [InlineData("WindowsQueue")]
    public void AcceptsSupportedConnectionTypesRegardlessOfCase(string connectionType)
    {
        var request = new CreatePrinterRequest(
            "Front desk", "HP", "LaserJet", connectionType, null, null, "{}");

        Assert.Empty(ShopDeviceRules.ValidatePrinter(request, request.CapabilitiesJson!));
    }

    [Fact]
    public void RejectsMissingPrinterIdentityAndUnsupportedConnection()
    {
        var request = new CreatePrinterRequest(
            " ", "HP", "Model", "Bluetooth", null, null, "{}");

        var errors = ShopDeviceRules.ValidatePrinter(request, "{}");

        Assert.Contains("printer", errors.Keys);
    }

    [Fact]
    public void RejectsCapabilitiesThatAreNotJsonObjectsOrExceedSixteenKb()
    {
        var request = new CreatePrinterRequest(
            "Front desk", "HP", "LaserJet", "USB", null, null, "[]");
        Assert.Contains("capabilitiesJson", ShopDeviceRules.ValidatePrinter(request, "[]").Keys);

        var tooLarge = "{\"value\":\"" + new string('x', 16001) + "\"}";
        Assert.Contains("capabilitiesJson", ShopDeviceRules.ValidatePrinter(request, tooLarge).Keys);
    }

    [Fact]
    public void TrimsAndValidatesQrStationDisplayNameLimit()
    {
        Assert.Empty(ShopDeviceRules.ValidateQrStationName(" Front desk "));
        Assert.Contains("displayName", ShopDeviceRules.ValidateQrStationName(" ").Keys);
        Assert.Contains("displayName", ShopDeviceRules.ValidateQrStationName(new string('x', 121)).Keys);
        Assert.Empty(ShopDeviceRules.ValidateQrStationName(new string('x', 120)));
    }

    [Theory]
    [InlineData("https://smartprint.example.test", true)]
    [InlineData("https://smartprint.example.test/", true)]
    [InlineData("http://smartprint.example.test", false)]
    [InlineData("https://smartprint.example.test/path", false)]
    [InlineData("https://user:pass@smartprint.example.test", false)]
    [InlineData("https://smartprint.example.test?tenant=1", false)]
    [InlineData("not-a-url", false)]
    public void AcceptsOnlyCanonicalPublicHttpsRoot(string value, bool expected)
    {
        Assert.Equal(expected, ShopDeviceRules.IsCanonicalPublicPortalBase(value));
    }
}
