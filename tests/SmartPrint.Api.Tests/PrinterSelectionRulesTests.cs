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
    public void AllowsOnlyFreshReadyQueuesOnAnOnlineAgent(string outputMode, string status)
    {
        Assert.True(PrinterSelectionRules.CanSelect(
            outputMode,
            status,
            Now.AddSeconds(-30),
            agentOnline: true,
            agentRevoked: false,
            agentLastSeenUtc: Now.AddSeconds(-30),
            now: Now));
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Paused")]
    [InlineData("Error")]
    [InlineData("Unknown")]
    public void KeepsNonReadyQueuesVisibleButDoesNotAllowSelection(string status)
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", status, Now, true, false, Now, Now));
    }

    [Theory]
    [InlineData("UNSUPPORTED_VIRTUAL")]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    public void DoesNotAllowUnsupportedOrUnknownOutputModes(string outputMode)
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            outputMode, "Ready", Now, true, false, Now, Now));
    }

    [Fact]
    public void RejectsAnOfflineOrRevokedAgent()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, false, false, Now, Now));
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, true, true, Now, Now));
    }

    [Fact]
    public void RejectsAnAgentHeartbeatOlderThanTwoMinutes()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, true, false, Now.AddMinutes(-2).AddTicks(-1), Now));
    }

    [Fact]
    public void RejectsAQueueReportOlderThanTwoMinutes()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now.AddMinutes(-2).AddTicks(-1), true, false, Now, Now));
    }

    [Fact]
    public void RejectsMissingAgentOrQueueTimestamps()
    {
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", Now, true, false, null, Now));
        Assert.False(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", null, true, false, Now, Now));
    }

    [Fact]
    public void AcceptsQueueAndAgentReportsExactlyTwoMinutesOldAtTheFreshnessBoundary()
    {
        var boundary = Now.AddMinutes(-2);
        Assert.True(PrinterSelectionRules.CanSelect(
            "PHYSICAL", "Ready", boundary, true, false, boundary, Now));
    }
}
