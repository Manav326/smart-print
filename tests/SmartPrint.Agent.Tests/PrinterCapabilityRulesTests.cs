using SmartPrint.Agent;
using Xunit;

namespace SmartPrint.Agent.Tests;

public sealed class PrinterCapabilityRulesTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    [InlineData(-2, false)]
    public void OnlyAnExplicitZeroCapabilityReportMeansUnsupported(int result, bool expected)
    {
        Assert.Equal(expected, PrinterCapabilityRules.IsExplicitlyUnsupportedCapability(result));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(257, 257)]
    public void MissingOrUnreasonablePaperReportsRemainUnknown(int reportedCount, int actualCount)
    {
        Assert.Null(PrinterCapabilityRules.EvaluatePaperSupport(
            reportedCount, actualCount, Array.Empty<short>(), 9));
    }

    [Fact]
    public void CompletePaperListDistinguishesSupportedFromExplicitlyUnsupported()
    {
        short[] supportedPapers = [1, 5, 8, 11];

        Assert.True(PrinterCapabilityRules.EvaluatePaperSupport(4, 4, supportedPapers, 8));
        Assert.False(PrinterCapabilityRules.EvaluatePaperSupport(4, 4, supportedPapers, 9));
    }

    
    [Fact]
    public void NupCapabilitiesAlwaysAllowOneUpAndOnlyReportedValuesAboveOne()
    {
        Assert.True(PrinterCapabilityRules.SupportsPagesPerSheet([1, 2, 4, 16], 1));
        Assert.True(PrinterCapabilityRules.SupportsPagesPerSheet([1, 2, 4, 16], 4));
        Assert.False(PrinterCapabilityRules.SupportsPagesPerSheet([1, 2, 4, 16], 8));
        Assert.False(PrinterCapabilityRules.SupportsPagesPerSheet([1, 2, 4, 16], 64));
    }

    [Fact]
    public void OrientationMustBeConfirmedExactlyBeforePrinting()
    {
        Assert.True(PrinterCapabilityRules.IsRequestedOrientationAccepted(true, 2, 2));
        Assert.False(PrinterCapabilityRules.IsRequestedOrientationAccepted(true, 1, 2));
        Assert.False(PrinterCapabilityRules.IsRequestedOrientationAccepted(false, 2, 2));
    }

    [Fact]
    public void IncompletePaperListRemainsUnknown()
    {
        short[] partialPapers = [1, 5];

        Assert.Null(PrinterCapabilityRules.EvaluatePaperSupport(4, 2, partialPapers, 9));
    }
}
