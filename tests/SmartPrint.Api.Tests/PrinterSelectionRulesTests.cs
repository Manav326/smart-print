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
    public void AllowsReadyOutputsOnAnOnlineAgent(string outputMode, string status)
    {
        Assert.True(PrinterSelectionRules.CanSelect(
            outputMode,
            status,
            Now.AddSeconds(-30),
            agentOnline: true,
            agentRevoked: false,
            agentLastSeenUtc: Now.AddSeconds(-30),
            now: Now, virtualTestGrantActive: false, isProduction: false));
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Paused")]
    [InlineData("Error")]
    [InlineData("Unknown")]
    public void KeepsNonReadyQueuesVisibleButDoesNotAllowSelection(string status)
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", status, Now, true, false, Now, Now, false, false));
    }

    [Theory]
    [InlineData("UNSUPPORTED_VIRTUAL")]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    public void DoesNotAllowUnsupportedOrUnknownOutputModes(string outputMode)
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            outputMode, "Ready", Now, true, false, Now, Now, false, false));
    }

    [Theory]
    [InlineData(true, "Ready", true)]
    [InlineData(true, "Busy", true)]
    [InlineData(true, "Offline", false)]
    [InlineData(false, "Ready", false)]
    public void VirtualQueueRequiresAnActiveGrantAndFreshReadyStatus(
        bool grantActive, string status, bool expected)
    {
        Assert.Equal(expected, PrinterSelectionRules.CanSelect(
            "UNSUPPORTED_VIRTUAL", status, Now.AddSeconds(-30), true, false,
            Now.AddSeconds(-30), Now, grantActive, false));
    }

    [Fact]
    public void RejectsAnOfflineOrRevokedAgent()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, false, false, Now, Now, false, false));
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, true, true, Now, Now, false, false));
    }

    [Fact]
    public void RejectsAnAgentHeartbeatOlderThanTwoMinutes()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, true, false, Now.AddMinutes(-2).AddTicks(-1), Now, false, false));
    }

    [Fact]
    public void RejectsAPhysicalQueueReportOlderThanTwoMinutes()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now.AddMinutes(-2).AddTicks(-1), true, false, Now, Now, false, false));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Busy")]
    [InlineData("Offline")]
    [InlineData("Unknown")]
    public void PdfExportDoesNotDependOnWindowsQueueStatusOrInventoryTimestamp(string status)
    {
        Assert.True(PrinterSelectionRules.CanSelect(
            "PDF_EXPORT", status, null, true, false, Now, Now, false, false));
        Assert.True(PrinterSelectionRules.CanSelect(
            "PDF_EXPORT", status, Now.AddMinutes(-30), true, false, Now, Now, false, false));
    }

    [Fact]
    public void PdfExportStillRequiresItsPairedAgentToBeOnline()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PDF_EXPORT", "Ready", null, false, false, Now, Now, false, false));
        Assert.False(PrinterSelectionRules.CanSelect(
            "PDF_EXPORT", "Ready", null, true, false, Now.AddMinutes(-3), Now, false, false));
    }

    [Fact]
    public void RejectsMissingAgentOrQueueTimestamps()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, true, false, null, Now, false, false));
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", null, true, false, Now, Now, false, false));
    }

    [Fact]
    public void AcceptsQueueAndAgentReportsExactlyTwoMinutesOldAtTheFreshnessBoundary()
    {
        var boundary = Now.AddMinutes(-2);
        Assert.True(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", boundary, true, false, boundary, Now, false, false));
    }
}
