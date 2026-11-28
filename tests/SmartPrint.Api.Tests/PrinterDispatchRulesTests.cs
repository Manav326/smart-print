using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrinterDispatchRulesTests
{
    [Theory]
    [InlineData("PHYSICAL", "Ready", true)]
    [InlineData("PHYSICAL", "Busy", true)]
    [InlineData("PHYSICAL", "Unknown", false)]
    [InlineData("PDF_EXPORT", "Ready", true)]
    [InlineData("PDF_EXPORT", "Busy", true)]
    [InlineData("PDF_EXPORT", "Unknown", true)]
    [InlineData("PDF_EXPORT", "AgentOffline", false)]
    [InlineData("PDF_EXPORT", "AgentRevoked", false)]
    [InlineData("PDF_EXPORT", "InventoryStale", false)]
    [InlineData("PDF_EXPORT", "Unavailable", false)]
    [InlineData("PHYSICAL", "Offline", false)]
    [InlineData("PHYSICAL", "Paused", false)]
    [InlineData("PHYSICAL", "Error", false)]
    [InlineData("UNSUPPORTED_VIRTUAL", "Ready", true)]
    [InlineData("UNSUPPORTED_VIRTUAL", "Busy", true)]
    [InlineData("UNSUPPORTED_VIRTUAL", "Offline", false)]
    public void AppliesOutputModeSpecificDispatchability(string outputMode, string status, bool expected)
    {
        Assert.Equal(expected, PrinterDispatchRules.IsDispatchableStatus(outputMode, status));
    }

    [Theory]
    [InlineData("PHYSICAL", "Ready")]
    [InlineData("PDF_EXPORT", "Ready")]
    [InlineData("UNSUPPORTED_VIRTUAL", "Ready")]
    public void PlatformHoldBlocksEveryOutputMode(string outputMode, string status)
    {
        Assert.False(PrinterDispatchRules.IsDispatchableStatus(outputMode, status, isPlatformHeld: true));
    }

    [Fact]
    public void FinalSpoolerAuthorisationMustMatchTheCurrentAttemptAndUnexpiredLease()
    {
        var now = new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);
        Assert.True(PrinterDispatchRules.IsCurrentSpoolerSubmissionAttempt(
            "VIRTUAL-TEST-123-A2", "VIRTUAL-TEST-123-A2", now.AddSeconds(1), now));
        Assert.False(PrinterDispatchRules.IsCurrentSpoolerSubmissionAttempt(
            "VIRTUAL-TEST-123-A2", "VIRTUAL-TEST-123-A1", now.AddSeconds(1), now));
        Assert.False(PrinterDispatchRules.IsCurrentSpoolerSubmissionAttempt(
            "VIRTUAL-TEST-123-A2", "VIRTUAL-TEST-123-A2", now, now));
        Assert.False(PrinterDispatchRules.IsCurrentSpoolerSubmissionAttempt(
            "VIRTUAL-TEST-123-A2", null, now.AddSeconds(1), now));
    }

    [Theory]
    [InlineData("PDF_EXPORT", true)]
    [InlineData("PHYSICAL", false)]
    [InlineData("pdf_export", true)]
    [InlineData("USB", false)]
    public void OnlyAgentSidePdfExportCanUseLastKnownInventory(string outputMode, bool expected)
    {
        Assert.Equal(expected, PrinterDispatchRules.CanDispatchWithStaleInventory(outputMode));
    }
}
