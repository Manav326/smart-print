using SmartPrint.Documents;

namespace SmartPrint.Agent.Tests;

public sealed class WindowsDefenderDocumentThreatScannerTests
{

    [Fact]
    public void AcceptsOnlyAnExplicitCleanResultWithExitCodeZero()
    {
        var result = WindowsDefenderDocumentThreatScanner.ClassifyResult(
            0,
            "Scan starting...\r\nScan finished.\r\nScanning C:\\ProgramData\\QRtoPrint\\SecurityScans\\test.pdf found no threats.\r\n");

        Assert.Null(result);
    }

    [Theory]
    [InlineData(0, "Scan starting... Scan finished.")]
    [InlineData(2, "Scanning C:\\scan.pdf found no threats.")]
    [InlineData(5, "Scanning C:\\scan.pdf found no threats.")]
    [InlineData(0, "Scannen beendet. Keine Bedrohungen gefunden.")]
    public void FailsClosedOnMissingErrorOrUnrecognisedCleanOutput(int exitCode, string output)
    {
        var result = WindowsDefenderDocumentThreatScanner.ClassifyResult(exitCode, output);

        Assert.NotNull(result);
        Assert.False(result.ThreatDetected);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void IdentifiesAnExplicitDefenderThreatBlock(int exitCode)
    {
        var output = """
            Scan starting...
            Scan finished.
            Scanning C:\ProgramData\QRtoPrint\SecurityScans\eicar.pdf found 1 threats.

            <===========================LIST OF DETECTED THREATS===========================>
            ----------------------------- Threat information ------------------------------
            Threat                  : Virus:DOS/EICAR_Test_File
            Resources               : 1 total
                file                : C:\ProgramData\QRtoPrint\SecurityScans\eicar.pdf
            -------------------------------------------------------------------------------
            """;

        var result = WindowsDefenderDocumentThreatScanner.ClassifyResult(exitCode, output);

        Assert.NotNull(result);
        Assert.True(result.ThreatDetected);
    }

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
