using SmartPrint.Documents;

namespace SmartPrint.Agent.Tests;

public sealed class WindowsDefenderDocumentThreatScannerTests
{
    [Fact]
    public async Task FailsClosedWhenScannedOutsideWindows()
    {
        if (OperatingSystem.IsWindows()) return;

        var scanner = new WindowsDefenderDocumentThreatScanner();
        var exception = await Assert.ThrowsAsync<DocumentThreatScanException>(() =>
            scanner.ScanAsync("sample"u8.ToArray(), ".pdf", CancellationToken.None));

        Assert.False(exception.ThreatDetected);
    }

    [Fact]
    public async Task RejectsUnsupportedExtensionsBeforeInvokingDefender()
    {
        if (!OperatingSystem.IsWindows()) return;

        var scanner = new WindowsDefenderDocumentThreatScanner();
        var exception = await Assert.ThrowsAsync<DocumentThreatScanException>(() =>
            scanner.ScanAsync("sample"u8.ToArray(), ".exe", CancellationToken.None));

        Assert.False(exception.ThreatDetected);
    }
}
