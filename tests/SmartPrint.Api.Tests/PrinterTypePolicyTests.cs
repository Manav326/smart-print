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
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void VirtualQueueTestOverrideIsOnlyEnabledOutsideProduction(
        bool configured, bool isProduction, bool expected)
    {
        Assert.Equal(expected, PrinterTypePolicy.IsTestModeEnabled(configured, isProduction));
    }

    [Theory]
    [InlineData("Ready", true)]
    [InlineData("Busy", true)]
    [InlineData("Offline", false)]
    [InlineData("Paused", false)]
    [InlineData("Error", false)]
    [InlineData("Unknown", false)]
    public void OnlyWorkingWindowsQueueStatesCanBeTemporarilyPromoted(string status, bool working)
    {
        var resolved = PrinterTypePolicy.ResolveOutputMode(
            PrinterTypePolicy.UnsupportedVirtual, status, treatWorkingVirtualQueuesAsPhysicalForTesting: true,
            connectionPort: "http://192.168.31.171:10631/p/virtual");

        Assert.Equal(working ? PrinterTypePolicy.Physical : PrinterTypePolicy.UnsupportedVirtual, resolved);
        Assert.Equal(working, PrinterTypePolicy.IsTestModeOverride(
            PrinterTypePolicy.UnsupportedVirtual,
            resolved,
            status,
            treatWorkingVirtualQueuesAsPhysicalForTesting: true,
            connectionPort: "http://192.168.31.171:10631/p/virtual"));
    }

    [Fact]
    public void DisabledTestPolicyNeverPromotesAnUnsupportedVirtualQueue()
    {
        Assert.Equal(
            PrinterTypePolicy.UnsupportedVirtual,
            PrinterTypePolicy.ResolveOutputMode(
                PrinterTypePolicy.UnsupportedVirtual, "Ready", treatWorkingVirtualQueuesAsPhysicalForTesting: false,
                connectionPort: "USB001"));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Busy")]
    [InlineData("Offline")]
    [InlineData("Unknown")]
    public void PdfExportAlwaysRemainsItsOwnOutputProfile(string status)
    {
        Assert.Equal(
            PrinterTypePolicy.PdfExport,
            PrinterTypePolicy.ResolveOutputMode(
                PrinterTypePolicy.PdfExport, status, treatWorkingVirtualQueuesAsPhysicalForTesting: true,
                connectionPort: "PORTPROMPT:"));
        Assert.False(PrinterTypePolicy.IsTestModeOverride(
            PrinterTypePolicy.PdfExport,
            PrinterTypePolicy.PdfExport,
            status,
            treatWorkingVirtualQueuesAsPhysicalForTesting: true,
            connectionPort: "PORTPROMPT:"));
    }

    [Theory]
    [InlineData("FILE:")]
    [InlineData("PORTPROMPT:")]
    [InlineData("NUL:")]
    [InlineData(null)]
    public void FileAndPromptSinksAreNeverPromotedEvenWhenWindowsReportsReady(string? port)
    {
        var resolved = PrinterTypePolicy.ResolveOutputMode(
            PrinterTypePolicy.UnsupportedVirtual,
            "Ready",
            treatWorkingVirtualQueuesAsPhysicalForTesting: true,
            connectionPort: port);

        Assert.Equal(PrinterTypePolicy.UnsupportedVirtual, resolved);
        Assert.False(PrinterTypePolicy.IsTestModeOverride(
            PrinterTypePolicy.UnsupportedVirtual,
            resolved,
            "Ready",
            treatWorkingVirtualQueuesAsPhysicalForTesting: true,
            connectionPort: port));
    }

    [Fact]
    public void PhysicalQueueClassificationDoesNotDependOnTheTestOverride()
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
