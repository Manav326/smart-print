using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrinterPlatformAccessPolicyTests
{
    [Theory]
    [InlineData("PHYSICAL")]
    [InlineData("PDF_EXPORT")]
    [InlineData("UNSUPPORTED_VIRTUAL")]
    public void EveryKnownOutputModeIsAvailableByDefault(string outputMode)
    {
        Assert.True(PrinterPlatformAccessPolicy.IsAvailable(isPlatformHeld: false));
        Assert.True(PrinterPlatformAccessPolicy.CanSelect(outputMode, isPlatformHeld: false));
    }

    [Theory]
    [InlineData("PHYSICAL")]
    [InlineData("PDF_EXPORT")]
    [InlineData("UNSUPPORTED_VIRTUAL")]
    public void ExplicitHoldBlocksEveryOutputMode(string outputMode)
    {
        Assert.False(PrinterPlatformAccessPolicy.IsAvailable(isPlatformHeld: true));
        Assert.False(PrinterPlatformAccessPolicy.CanSelect(outputMode, isPlatformHeld: true));
        Assert.False(PrinterPlatformAccessPolicy.CanRunDiagnostic(outputMode, isPlatformHeld: true));
        Assert.False(PrinterPlatformAccessPolicy.CanAssign(isPlatformHeld: true));
        Assert.False(PrinterPlatformAccessPolicy.CanDispatch(isPlatformHeld: true));
    }

    [Theory]
    [InlineData("PHYSICAL", true)]
    [InlineData("UNSUPPORTED_VIRTUAL", true)]
    [InlineData("PDF_EXPORT", false)]
    public void DiagnosticsAreAvailableForSupportedQueueTypesOnly(string outputMode, bool expected)
    {
        Assert.Equal(expected, PrinterPlatformAccessPolicy.CanRunDiagnostic(outputMode, isPlatformHeld: false));
    }

    [Theory]
    [InlineData("PHYSICAL", "PHYSICAL", "CUSTOMER_QR", false, true)]
    [InlineData("PHYSICAL", "TEST_VIRTUAL", "CUSTOMER_QR", false, false)]
    [InlineData("UNSUPPORTED_VIRTUAL", "TEST_VIRTUAL", "CUSTOMER_QR", false, true)]
    [InlineData("UNSUPPORTED_VIRTUAL", "PHYSICAL", "CUSTOMER_QR", false, false)]
    [InlineData("UNSUPPORTED_VIRTUAL", "TEST_VIRTUAL", "PRINTER_DIAGNOSTIC", false, true)]
    [InlineData("UNSUPPORTED_VIRTUAL", "TEST_VIRTUAL", "SHOP_OWNER", false, true)]
    [InlineData("UNSUPPORTED_VIRTUAL", "TEST_VIRTUAL", "CUSTOMER_QR", true, false)]
    [InlineData("PHYSICAL", "PHYSICAL", "CUSTOMER_QR", true, false)]
    [InlineData("UNSUPPORTED_VIRTUAL", "TEST_VIRTUAL", "UNKNOWN", false, false)]
    public void SpoolerAuthorizationPreservesOutputClassificationOriginAndHold(
        string outputMode, string outputType, string origin, bool held, bool expected)
    {
        Assert.Equal(expected, PrinterPlatformAccessPolicy.CanAuthorizeSpoolerSubmission(
            outputMode, outputType, held, origin));
    }
}
