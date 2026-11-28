using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrinterTypePolicyTests
{
    [Theory]
    [InlineData("USB001", null, null, "USB")]
    [InlineData("DOT4_001", null, null, "USB")]
    [InlineData("IP_192.168.31.171", null, null, "LAN")]
    [InlineData("TCP_192.168.31.171", null, null, "LAN")]
    [InlineData("LPR_192.168.31.171", null, null, "LAN")]
    [InlineData(@"\\PRINT-SERVER\Queue", null, null, "Shared")]
    [InlineData("WSD-abc123", null, null, "LAN")]
    [InlineData("WSD-abc123", "Microsoft IPP Class Driver", null, "IPP")]
    [InlineData("http://192.168.31.171:10631/p/virtual", "Microsoft IPP Class Driver", null, "IPP")]
    [InlineData("http://192.168.31.171:10631/p/virtual", "Generic Network Driver", null, "LAN")]
    [InlineData("https://printer.local/ipp/print", "Microsoft IPP Class Driver", null, "IPP")]
    [InlineData("ipp://printer.local/ipp/print", null, null, "IPP")]
    [InlineData("ipps://printer.local/ipp/print", null, null, "IPP")]
    [InlineData("PORTPROMPT:", null, null, "WindowsQueue")]
    [InlineData("FILE:", null, null, "WindowsQueue")]
    [InlineData("ipps://printer.local/ipp/print", "Microsoft IPP Class Driver", @"\\PRINT-SERVER\Queue", "Shared")]
    public void DetectsConnectionTransportWithoutChangingOutputKind(
        string port, string? driver, string? queue, string expected)
    {
        Assert.Equal(expected, PrinterTypePolicy.ResolveConnectionType(port, driver, queue));
    }

    [Fact]
    public void DetectsSharedQueueFromUncQueueIdentityWhenWindowsUsesAnOpaquePort()
    {
        Assert.Equal(
            "Shared",
            PrinterTypePolicy.ResolveConnectionType(
                "Ne00:",
                "HP Universal Printing PCL 6",
                @"\\PRINT-SERVER\HP-LaserJet"));
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
