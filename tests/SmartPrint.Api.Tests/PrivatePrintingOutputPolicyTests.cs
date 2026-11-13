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

    [Fact]
    public void VirtualPrinterIsDeniedByDefault()
    {
        Assert.False(PrivatePrintingOutputPolicy.IsEligible(
            allowVirtualOutput: false, isProduction: false, PrivatePrintingOutputPolicy.PdfExport));
    }

    [Fact]
    public void VirtualPrinterRequiresCentralNonProductionOptIn()
    {
        Assert.True(PrivatePrintingOutputPolicy.IsEligible(
            allowVirtualOutput: true, isProduction: false, PrivatePrintingOutputPolicy.PdfExport));
        Assert.True(PrivatePrintingOutputPolicy.IsVirtualTestOutput(
            allowVirtualOutput: true, isProduction: false, PrivatePrintingOutputPolicy.PdfExport));
    }

    [Fact]
    public void ProductionAlwaysRejectsVirtualPrinterEvenWhenSettingIsEnabled()
    {
        Assert.False(PrivatePrintingOutputPolicy.IsEligible(
            allowVirtualOutput: true, isProduction: true, PrivatePrintingOutputPolicy.PdfExport));
        Assert.False(PrivatePrintingOutputPolicy.IsVirtualTestOutput(
            allowVirtualOutput: true, isProduction: true, PrivatePrintingOutputPolicy.PdfExport));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    [InlineData("PDF")]
    public void UnknownPrinterModesAreNeverEligible(string? outputMode)
    {
        Assert.False(PrivatePrintingOutputPolicy.IsEligible(
            allowVirtualOutput: true, isProduction: false, outputMode));
        Assert.False(PrivatePrintingOutputPolicy.IsVirtualTestOutput(
            allowVirtualOutput: true, isProduction: false, outputMode));
    }

    [Theory]
    [InlineData("physical")]
    [InlineData("PHYSICAL")]
    [InlineData("Physical")]
    public void PhysicalOutputModeComparisonIsCaseInsensitive(string outputMode)
    {
        Assert.True(PrivatePrintingOutputPolicy.IsEligible(
            allowVirtualOutput: false, isProduction: true, outputMode));
    }
}
