using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrinterTypePolicyTests
{
    [Theory]
    [InlineData("USB001", "USB")]
    [InlineData("IP_192.168.31.171", "LAN")]
    [InlineData(@"\\PRINT-SERVER\Queue", "LAN")]
    [InlineData("WSD-abc123", "LAN")]
    [InlineData("http://192.168.31.171:10631/p/virtual", "LAN")]
    [InlineData("https://printer.local/print", "LAN")]
    [InlineData("ipp://printer.local/ipp/print", "IPP")]
    [InlineData("ipps://printer.local/ipp/print", "IPP")]
    [InlineData("PORTPROMPT:", "WindowsQueue")]
    [InlineData("FILE:", "WindowsQueue")]
    public void DetectsConnectionTransportWithoutChangingOutputKind(string port, string expected)
    {
        Assert.Equal(expected, PrinterTypePolicy.ResolveConnectionType(port));
    }

    [Fact]
    public void VirtualQueueIdentityIsNeverPromoted()
    {
        Assert.Equal(PrinterTypePolicy.UnsupportedVirtual,
            PrinterTypePolicy.ResolveOutputMode(PrinterTypePolicy.UnsupportedVirtual));
    }

    [Fact]
    public void PdfExportAlwaysRemainsItsOwnOutputProfile()
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
    [Theory]
    [InlineData("USB001")]
    [InlineData("IP_192.168.1.20")]
    [InlineData("WSD-12345678")]
    [InlineData(@"\\PRINT-SERVER\Office")]
    [InlineData("LPT1:")]
    public void PhysicalAndSharedQueuePortsRemainConnectedTargets(string port)
    {
        Assert.True(PrinterTypePolicy.IsConnectedPrinterPort(port));
    }

    [Theory]
    [InlineData("Ready", true)]
    [InlineData("Busy", true)]
    [InlineData("Offline", false)]
    [InlineData("Paused", false)]
    [InlineData("Unknown", false)]
    [InlineData(null, false)]
    public void OnlyReadyOrBusyWindowsQueuesAreWorking(string? status, bool expected)
    {
        Assert.Equal(expected, PrinterTypePolicy.IsWorkingWindowsQueue(status));
    }

}
