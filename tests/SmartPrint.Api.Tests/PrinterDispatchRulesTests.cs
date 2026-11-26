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
    public void AppliesOutputModeSpecificDispatchability(string outputMode, string status, bool expected)
    {
        Assert.Equal(expected, PrinterDispatchRules.IsDispatchableStatus(outputMode, status));
    }

    [Theory]
    [InlineData("UNSUPPORTED_VIRTUAL", "Ready", false, false, false)]
    [InlineData("UNSUPPORTED_VIRTUAL", "Busy", true, false, true)]
    [InlineData("UNSUPPORTED_VIRTUAL", "Ready", false, true, false)]
    [InlineData("UNSUPPORTED_VIRTUAL", "Offline", true, false, false)]
    [InlineData("PHYSICAL", "Ready", false, true, true)]
    public void VirtualQueueDispatchRequiresGrantAndNonProduction(
        string outputMode, string status, bool grantActive, bool isProduction, bool expected)
    {
        Assert.Equal(expected, PrinterDispatchRules.IsDispatchableStatus(
            outputMode, status, grantActive, isProduction));
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
