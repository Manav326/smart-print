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
    [Theory]
    [InlineData("35x45", 350, 450)]
    [InlineData("25x35", 250, 350)]
    [InlineData("2x2", 508, 508)]
    [InlineData("4x6", 1016, 1524)]
    public void MapsSupportedPhysicalPhotoSizesToWindowsCustomMedia(
        string sizeCode, short expectedWidth, short expectedHeight)
    {
        Assert.True(PrinterCapabilityRules.TryGetPhotoPaperDimensions(
            sizeCode, out var width, out var height));
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Theory]
    [InlineData("")]
    [InlineData("passport")]
    [InlineData("10x15")]
    public void RejectsUnsupportedPhysicalPhotoSizes(string sizeCode)
    {
        Assert.False(PrinterCapabilityRules.TryGetPhotoPaperDimensions(sizeCode, out var width, out var height));
        Assert.Equal((short)0, width);
        Assert.Equal((short)0, height);
    }

    [Fact]
    public void SelectsOnlyAnUnambiguousDriverPhotoMediaType()
    {
        (short Code, string Name)[] media =
        [
            (1, "Plain paper"),
            (2, "Glossy Photo Paper"),
            (3, "Matte Photo Paper")
        ];

        Assert.Equal((short)2, PrinterCapabilityRules.SelectPhotoMediaTypeCode(media, "GLOSSY"));
        Assert.Equal((short)3, PrinterCapabilityRules.SelectPhotoMediaTypeCode(media, "MATTE"));
        Assert.Null(PrinterCapabilityRules.SelectPhotoMediaTypeCode(media, "PLAIN"));
    }

    [Fact]
    public void AmbiguousOrMissingDriverPhotoMediaTypesRemainUnsupported()
    {
        (short Code, string Name)[] ambiguous =
        [
            (2, "Glossy Photo"),
            (7, "Premium Glossy")
        ];

        Assert.Null(PrinterCapabilityRules.SelectPhotoMediaTypeCode(ambiguous, "GLOSSY"));
        Assert.Null(PrinterCapabilityRules.SelectPhotoMediaTypeCode(Array.Empty<(short, string)>(), "MATTE"));
    }

    [Fact]
    public void PhotoPaperMustBeAcceptedAtExactDimensionsAndMediaType()
    {
        Assert.True(PrinterCapabilityRules.IsRequestedPhotoPaperSizeAccepted(
            true, true, true, 0, 350, 450, 350, 450));
        Assert.False(PrinterCapabilityRules.IsRequestedPhotoPaperSizeAccepted(
            true, true, true, 0, 350, 451, 350, 450));
        Assert.False(PrinterCapabilityRules.IsRequestedPhotoPaperSizeAccepted(
            true, false, true, 0, 350, 450, 350, 450));

        Assert.True(PrinterCapabilityRules.IsRequestedPhotoMediaTypeAccepted(true, 2, 2));
        Assert.False(PrinterCapabilityRules.IsRequestedPhotoMediaTypeAccepted(true, 3, 2));
        Assert.False(PrinterCapabilityRules.IsRequestedPhotoMediaTypeAccepted(false, 2, 2));
    }

}
