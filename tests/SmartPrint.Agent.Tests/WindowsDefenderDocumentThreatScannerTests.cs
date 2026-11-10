using System.Text;
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
    [Trait("Category", "WindowsDefenderIntegration")]
    public async Task RealDefenderAcceptsCleanContentAndBlocksEicar()
    {
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(
                Environment.GetEnvironmentVariable("SMARTPRINT_RUN_WINDOWS_DEFENDER_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var scanner = new WindowsDefenderDocumentThreatScanner();
        var cleanDocument = Encoding.ASCII.GetBytes(
            "%PDF-1.7\n% SmartPrint clean Defender integration fixture\n%%EOF\n");
        await scanner.ScanAsync(cleanDocument, ".pdf", CancellationToken.None);

        // Keep the signature fragmented in source so Defender does not flag the
        // compiled test assembly itself; only the temporary scan file contains it.
        var eicarText = string.Concat(
            "X5O!P%@AP[4\\P",
            "ZX54(P^)7CC)7}",
            "$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        var eicarDocument = Encoding.ASCII.GetBytes(eicarText);

        var rejection = await Assert.ThrowsAsync<DocumentThreatScanException>(() =>
            scanner.ScanAsync(eicarDocument, ".pdf", CancellationToken.None));

        // A real-time Defender block during staging or an explicit MpCmdRun
        // threat report are both safe rejection outcomes. A clean result is not.
        Console.WriteLine(
            $"Real Defender rejected the EICAR fixture. ExplicitThreatDetection={rejection.ThreatDetected}");
        Assert.NotNull(rejection);
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
