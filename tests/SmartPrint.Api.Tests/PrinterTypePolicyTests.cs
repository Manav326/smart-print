using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrinterTypePolicyTests
{
    [Theory]
    [InlineData("USB001", "USB")]
    [InlineData("IP_192.168.31.171", "LAN")]
    [InlineData(@"\\PRINT-SERVER\Queue", "LAN")]
    [InlineData("WSD-abc123", "LAN")]
    [InlineData("http://192.168.31.171:10631/p/virtual", "IPP")]
    [InlineData("ipp://printer.local/ipp/print", "IPP")]
    [InlineData("ipps://printer.local/ipp/print", "IPP")]
    [InlineData("PORTPROMPT:", "WindowsQueue")]
    [InlineData("FILE:", "WindowsQueue")]
    public void DetectsConnectionTransportWithoutChangingOutputKind(string port, string expected)
    {
        Assert.Equal(expected, PrinterTypePolicy.ResolveConnectionType(port));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyGlobalOverrideIsAlwaysInert(bool configured, bool isProduction)
    {
        Assert.False(PrinterTypePolicy.IsTestModeEnabled(configured, isProduction));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Busy")]
    [InlineData("Offline")]
    [InlineData("Paused")]
    [InlineData("Error")]
    [InlineData("Unknown")]
    public void VirtualQueueIdentityIsNeverPromotedByGlobalSettings(string status)
    {
        var resolved = PrinterTypePolicy.ResolveOutputMode(
            PrinterTypePolicy.UnsupportedVirtual, status, treatWorkingVirtualQueuesAsPhysicalForTesting: true,
            connectionPort: "http://192.168.31.171:10631/p/virtual");

        Assert.Equal(PrinterTypePolicy.UnsupportedVirtual, resolved);
        Assert.False(PrinterTypePolicy.IsTestModeOverride(
            PrinterTypePolicy.UnsupportedVirtual, resolved, status,
            treatWorkingVirtualQueuesAsPhysicalForTesting: true,
            connectionPort: "http://192.168.31.171:10631/p/virtual"));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Busy")]
    [InlineData("Unknown")]
    public void PdfExportAlwaysRemainsItsOwnOutputProfile(string status)
    {
        Assert.Equal(
            PrinterTypePolicy.PdfExport,
            PrinterTypePolicy.ResolveOutputMode(
                PrinterTypePolicy.PdfExport, status, treatWorkingVirtualQueuesAsPhysicalForTesting: true,
                connectionPort: "PORTPROMPT:"));
    }

    [Theory]
    [InlineData("FILE:")]
    [InlineData("PORTPROMPT:")]
    [InlineData("NUL:")]
    [InlineData(null)]
    public void FileAndPromptSinksAreNotConnectedPrinterPorts(string? port)
    {
        Assert.False(PrinterTypePolicy.IsConnectedPrinterPort(port));
    }

    [Fact]
    public void PhysicalQueueClassificationDoesNotDependOnTestSettings()
    {
        Assert.Equal(
            PrinterTypePolicy.Physical,
            PrinterTypePolicy.ResolveOutputMode(
                PrinterTypePolicy.Physical, "Ready", treatWorkingVirtualQueuesAsPhysicalForTesting: false));
    }

    [Theory]
    [InlineData("PHYSICAL", true, true)]
    [InlineData("PDF_EXPORT", true, false)]
    [InlineData("UNSUPPORTED_VIRTUAL", false, true)]
    [InlineData("UNKNOWN", false, true)]
    public void DefinesWhichProfilesUseTheWindowsQueueReadinessContract(
        string outputMode, bool selectable, bool usesQueueReadiness)
    {
        Assert.Equal(selectable, PrinterTypePolicy.IsSelectableOutputMode(outputMode));
        Assert.Equal(usesQueueReadiness, PrinterTypePolicy.UsesWindowsQueueReadiness(outputMode));
    }
}
