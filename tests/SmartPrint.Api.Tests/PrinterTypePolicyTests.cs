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
    [InlineData("Ready")]
    [InlineData("Busy")]
    [InlineData("Offline")]
    [InlineData("Paused")]
    [InlineData("Error")]
    [InlineData("Unknown")]
    public void VirtualQueueIdentityIsNeverPromoted(string status)
    {
        Assert.Equal(PrinterTypePolicy.UnsupportedVirtual,
            PrinterTypePolicy.ResolveOutputMode(PrinterTypePolicy.UnsupportedVirtual));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Busy")]
    [InlineData("Unknown")]
    public void PdfExportAlwaysRemainsItsOwnOutputProfile(string status)
    {
        Assert.Equal(PrinterTypePolicy.PdfExport,
            PrinterTypePolicy.ResolveOutputMode(PrinterTypePolicy.PdfExport));
    }

    [Fact]
    public void PhysicalQueueClassificationIsStable()
    {
        Assert.Equal(PrinterTypePolicy.Physical,
            PrinterTypePolicy.ResolveOutputMode(PrinterTypePolicy.Physical));
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
