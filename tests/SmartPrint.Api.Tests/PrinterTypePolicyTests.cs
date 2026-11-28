using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrinterTypePolicyTests
{
    [Theory]
    [InlineData("USB001", null, "USB")]
    [InlineData("IP_192.168.31.171", null, "LAN")]
    [InlineData("TCP_192.168.31.171", null, "LAN")]
    [InlineData("LPR_192.168.31.171", null, "LAN")]
    [InlineData(@"\\PRINT-SERVER\Queue", null, "Shared")]
    [InlineData("WSD-abc123", null, "LAN")]
    [InlineData("WSD-abc123", "Microsoft IPP Class Driver", "IPP")]
    [InlineData("http://192.168.31.171:10631/p/virtual", "Microsoft IPP Class Driver", "IPP")]
    [InlineData("http://192.168.31.171:10631/p/virtual", "Generic Network Driver", "LAN")]
    [InlineData("https://printer.local/ipp/print", "Microsoft IPP Class Driver", "IPP")]
    [InlineData("ipp://printer.local/ipp/print", null, "IPP")]
    [InlineData("ipps://printer.local/ipp/print", null, "IPP")]
    [InlineData("PORTPROMPT:", null, "WindowsQueue")]
    [InlineData("FILE:", null, "WindowsQueue")]
    public void DetectsConnectionTransportWithoutChangingOutputKind(string port, string? driver, string expected)
    {
        Assert.Equal(expected, PrinterTypePolicy.ResolveConnectionType(port, driver));
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
}
