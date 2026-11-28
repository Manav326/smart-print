using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrivatePrintingRelayStagingRulesTests
{
    [Fact]
    public void ConnectedAgentCanSecureAFileWithoutRequiringPrinterReadyState()
    {
        // This rule intentionally receives no printer runtime status. A printer
        // that is Offline/Paused can still have its authorised document staged on
        // the shop computer; the print-dispatch path checks hardware readiness.
        Assert.True(PrivatePrintingRelayStagingRules.CanStage(
            agentConnected: true,
            printerAssignedToAgent: true,
            printerActive: true,
            queueConfigured: true,
            privateOutputEligible: true));
    }

    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    public void StagingFailsClosedWhenAnySecurityOrAssignmentRequirementFails(
        bool agentConnected,
        bool printerAssignedToAgent,
        bool printerActive,
        bool queueConfigured,
        bool privateOutputEligible)
    {
        Assert.False(PrivatePrintingRelayStagingRules.CanStage(
            agentConnected,
            printerAssignedToAgent,
            printerActive,
            queueConfigured,
            privateOutputEligible));
    }

    [Theory]
    [InlineData(true, true, PrivatePrintingOutputPolicy.Physical, true)]
    [InlineData(true, false, PrivatePrintingOutputPolicy.PdfExport, false)]
    [InlineData(false, true, PrivatePrintingOutputPolicy.PdfExport, true)]
    [InlineData(false, true, PrinterTypePolicy.UnsupportedVirtual, true)]
    [InlineData(false, false, PrinterTypePolicy.UnsupportedVirtual, false)]
    public void PhysicalAndVirtualOutputRestrictionsStillApply(
        bool isProduction,
        bool allowVirtualOutput,
        string outputMode,
        bool expected)
    {
        Assert.Equal(expected, PrivatePrintingOutputPolicy.IsEligible(
            allowVirtualOutput, isProduction, outputMode));
    }
}
