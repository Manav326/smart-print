using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrinterPagesPerSheetRulesTests
{
    [Theory]
    [InlineData("RightBottom")]
    [InlineData("BottomRight")]
    [InlineData("LeftBottom")]
    [InlineData("BottomLeft")]
    [InlineData("RightTop")]
    [InlineData("TopRight")]
    [InlineData("LeftTop")]
    [InlineData("TopLeft")]
    public void AcceptsOnlyPageCountsAndDirectionsReportedByTheDriver(string direction)
    {
        var capabilities = $$"""{"pagesPerSheet":[1,2,4,6,9,16],"pagesPerSheetDirection":"{{direction}}"}""";

        Assert.True(PrinterPagesPerSheetRules.Supports(capabilities, 2));
        Assert.True(PrinterPagesPerSheetRules.Supports(capabilities, 4));
        Assert.False(PrinterPagesPerSheetRules.Supports(capabilities, 8));
    }

    [Fact]
    public void RejectsNupWhenTheDriverDoesNotReportPresentationDirection()
    {
        const string capabilities = """{"pagesPerSheet":[1,2,4]}""";

        Assert.False(PrinterPagesPerSheetRules.Supports(capabilities, 2));
        Assert.False(PrinterPagesPerSheetRules.Supports(capabilities, 4));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("TopLeftToRight")]
    public void RejectsUnknownPresentationDirection(string direction)
    {
        var capabilities = $$"""{"pagesPerSheet":[1,2,4],"pagesPerSheetDirection":"{{direction}}"}""";

        Assert.False(PrinterPagesPerSheetRules.Supports(capabilities, 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void RejectsInvalidPagesPerSheetEvenWhenTheDirectionIsSupported(int requested)
    {
        const string capabilities = """{"pagesPerSheet":[1,2,4],"pagesPerSheetDirection":"RightBottom"}""";

        Assert.False(PrinterPagesPerSheetRules.Supports(capabilities, requested));
    }

    [Fact]
    public void OneUpRemainsAvailableWithoutAnyMultiPageCapabilities()
    {
        Assert.True(PrinterPagesPerSheetRules.Supports("{}", 1));
        Assert.False(PrinterPagesPerSheetRules.Supports("{}", 2));
    }

    [Fact]
    public void RejectsMalformedCapabilityJson()
    {
        Assert.False(PrinterPagesPerSheetRules.Supports("{", 4));
    }
}
