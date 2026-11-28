using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrivatePrintingOutputPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PhysicalPrintersAreAlwaysEligible(bool allowVirtualOutput, bool isProduction)
    {
        Assert.True(PrivatePrintingOutputPolicy.IsEligible(
            allowVirtualOutput, isProduction, PrivatePrintingOutputPolicy.Physical));
        Assert.False(PrivatePrintingOutputPolicy.IsVirtualTestOutput(
            allowVirtualOutput, isProduction, PrivatePrintingOutputPolicy.Physical));
    }

    [Theory]
    [InlineData("PDF_EXPORT")]
    [InlineData("UNSUPPORTED_VIRTUAL")]
    public void DigitalDestinationsRequireTheCentralNonProductionSetting(string outputMode)
    {
        Assert.False(PrivatePrintingOutputPolicy.IsEligible(false, false, outputMode));
        Assert.False(PrivatePrintingOutputPolicy.IsEligible(true, true, outputMode));
        Assert.True(PrivatePrintingOutputPolicy.IsEligible(true, false, outputMode));
        Assert.False(PrivatePrintingOutputPolicy.IsVirtualTestOutput(false, false, outputMode));
        Assert.True(PrivatePrintingOutputPolicy.IsVirtualTestOutput(true, false, outputMode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    [InlineData("PDF")]
    public void UnknownPrinterModesAreNeverEligible(string? outputMode)
    {
        Assert.False(PrivatePrintingOutputPolicy.IsEligible(true, false, outputMode));
        Assert.False(PrivatePrintingOutputPolicy.IsVirtualTestOutput(true, false, outputMode));
    }

    [Theory]
    [InlineData("physical")]
    [InlineData("PHYSICAL")]
    [InlineData("Physical")]
    public void PhysicalOutputModeComparisonIsCaseInsensitive(string outputMode)
    {
        Assert.True(PrivatePrintingOutputPolicy.IsEligible(false, true, outputMode));
    }
}
