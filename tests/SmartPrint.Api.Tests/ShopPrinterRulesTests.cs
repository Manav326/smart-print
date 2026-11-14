using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopPrinterRulesTests
{
    private static CreatePrinterRequest Request(
        string name = " Office Printer ",
        string manufacturer = " Canon ",
        string model = " iR-ADV ",
        string connection = "windowsqueue",
        string? capabilities = null) =>
        new(name, manufacturer, model, connection, null, null, capabilities);

    [Fact]
    public void NormalizesConnectionTypeAndDefaultsMissingCapabilities()
    {
        var failure = ShopPrinterRules.Validate(Request(), out var capabilities, out var connection);

        Assert.Equal(ShopPrinterValidationFailure.None, failure);
        Assert.Equal("{}", capabilities);
        Assert.Equal("WindowsQueue", connection);
    }

    [Theory]
    [InlineData("", "Canon", "Model", "USB")]
    [InlineData("Name", "", "Model", "USB")]
    [InlineData("Name", "Canon", "", "USB")]
    [InlineData("Name", "Canon", "Model", "Unsupported")]
    public void RejectsMissingOrUnsupportedPrinterDetails(
        string name,
        string manufacturer,
        string model,
        string connection)
    {
        Assert.Equal(
            ShopPrinterValidationFailure.InvalidPrinter,
            ShopPrinterRules.Validate(Request(name, manufacturer, model, connection), out _, out _));
    }

    [Fact]
    public void EnforcesPrinterFieldLengthLimits()
    {
        Assert.Equal(ShopPrinterValidationFailure.InvalidPrinter,
            ShopPrinterRules.Validate(Request(name: new string('n', 161)), out _, out _));
        Assert.Equal(ShopPrinterValidationFailure.InvalidPrinter,
            ShopPrinterRules.Validate(Request(manufacturer: new string('m', 81)), out _, out _));
        Assert.Equal(ShopPrinterValidationFailure.InvalidPrinter,
            ShopPrinterRules.Validate(Request(model: new string('m', 121)), out _, out _));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void RejectsInvalidOrNonObjectCapabilities(string capabilities)
    {
        Assert.Equal(
            ShopPrinterValidationFailure.InvalidCapabilities,
            ShopPrinterRules.Validate(Request(capabilities: capabilities), out _, out _));
    }

    [Fact]
    public void RejectsCapabilitiesOverSixteenKilobytes()
    {
        var capabilities = "{\"value\":\"" + new string('x', 16000) + "\"}";
        Assert.Equal(
            ShopPrinterValidationFailure.InvalidCapabilities,
            ShopPrinterRules.Validate(Request(capabilities: capabilities), out _, out _));
    }

    [Theory]
    [InlineData("USB", "USB")]
    [InlineData("lan", "LAN")]
    [InlineData("IPP", "IPP")]
    [InlineData("windowsqueue", "WindowsQueue")]
    public void AcceptsSupportedConnectionTypesCaseInsensitively(string supplied, string expected)
    {
        Assert.Equal(ShopPrinterValidationFailure.None,
            ShopPrinterRules.Validate(Request(connection: supplied), out _, out var actual));
        Assert.Equal(expected, actual);
    }
}
