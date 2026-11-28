using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrinterSelectionRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 7, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("PHYSICAL", "Ready")]
    [InlineData("PHYSICAL", "Busy")]
    [InlineData("PDF_EXPORT", "Ready")]
    [InlineData("PDF_EXPORT", "Busy")]
    [InlineData("UNSUPPORTED_VIRTUAL", "Ready")]
    [InlineData("UNSUPPORTED_VIRTUAL", "Busy")]
    public void AllowsSupportedReadyOutputModesOnAnOnlineAgent(string outputMode, string status)
    {
        Assert.True(PrinterSelectionRules.CanSelect(
            outputMode,
            status,
            Now.AddSeconds(-30),
            agentOnline: true,
            agentRevoked: false,
            agentLastSeenUtc: Now.AddSeconds(-30),
            now: Now,
            isPlatformHeld: false));
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Paused")]
    [InlineData("Error")]
    [InlineData("Unknown")]
    public void KeepsNonReadyWindowsQueuesVisibleButDoesNotAllowSelection(string status)
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", status, Now, true, false, Now, Now, false));
        Assert.False(PrinterSelectionRules.CanSelect(
            "UNSUPPORTED_VIRTUAL", status, Now, true, false, Now, Now, false));
    }

    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    public void DoesNotAllowUnknownOutputModes(string outputMode)
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            outputMode, "Ready", Now, true, false, Now, Now, false));
    }

    [Theory]
    [InlineData("PHYSICAL")]
    [InlineData("PDF_EXPORT")]
    [InlineData("UNSUPPORTED_VIRTUAL")]
    public void PlatformHoldBlocksEveryOutputMode(string outputMode)
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            outputMode, "Ready", Now, true, false, Now, Now, isPlatformHeld: true));
    }

    [Fact]
    public void RejectsAnOfflineOrRevokedAgent()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, false, false, Now, Now, false));
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, true, true, Now, Now, false));
    }

    [Fact]
    public void RejectsAnAgentHeartbeatOlderThanTwoMinutes()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, true, false, Now.AddMinutes(-2).AddTicks(-1), Now, false));
    }

    [Fact]
    public void RejectsAPhysicalQueueReportOlderThanTwoMinutes()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now.AddMinutes(-2).AddTicks(-1), true, false, Now, Now, false));
        Assert.False(PrinterSelectionRules.CanSelect(
            "UNSUPPORTED_VIRTUAL", "Ready", Now.AddMinutes(-2).AddTicks(-1), true, false, Now, Now, false));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Busy")]
    [InlineData("Offline")]
    [InlineData("Unknown")]
    public void PdfExportDoesNotDependOnWindowsQueueStatusOrInventoryTimestamp(string status)
    {
        Assert.True(PrinterSelectionRules.CanSelect(
            "PDF_EXPORT", status, null, true, false, Now, Now, false));
        Assert.True(PrinterSelectionRules.CanSelect(
            "PDF_EXPORT", status, Now.AddMinutes(-30), true, false, Now, Now, false));
    }

    [Fact]
    public void PdfExportStillRequiresItsPairedAgentToBeOnline()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PDF_EXPORT", "Ready", null, false, false, Now, Now, false));
        Assert.False(PrinterSelectionRules.CanSelect(
            "PDF_EXPORT", "Ready", null, true, false, Now.AddMinutes(-3), Now, false));
    }

    [Fact]
    public void RejectsMissingAgentOrQueueTimestamps()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, true, false, null, Now, false));
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", null, true, false, Now, Now, false));
    }

    [Fact]
    public void AcceptsQueueAndAgentReportsExactlyTwoMinutesOldAtTheFreshnessBoundary()
    {
        var boundary = Now.AddMinutes(-2);
        Assert.True(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", boundary, true, false, boundary, Now, false));
    }
}
