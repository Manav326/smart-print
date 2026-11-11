using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class IdCardPdfRulesTests
{
    private const double A4WidthPoints = 210d * 72d / 25.4d;
    private const double A4HeightPoints = 297d * 72d / 25.4d;

    [Fact]
    public void AcceptsOnePortraitA4PdfPage()
    {
        Assert.True(IdCardPdfRules.HasExactlyA4PortraitPage(Page(A4WidthPoints, A4HeightPoints)));
        // Android PdfDocument currently uses integer point page dimensions.
        Assert.True(IdCardPdfRules.HasExactlyA4PortraitPage(Page(595, 842)));
    }

    [Theory]
    [InlineData(595.28, 792.00)]
    [InlineData(841.89, 595.28)]
    [InlineData(500.00, 700.00)]
    [InlineData(595.28, 842.00 + 4)]
    public void RejectsLetterLandscapeOrWrongPageBoxes(double width, double height)
    {
        Assert.False(IdCardPdfRules.HasExactlyA4PortraitPage(Page(width, height)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[\"invalid\"]")]
    [InlineData("[{}]")]
    [InlineData("[{\"widthPoints\":\"595\",\"heightPoints\":842}]")]
    [InlineData("not-json")]
    public void RejectsMalformedOrUnexpectedPageMetadata(string json)
    {
        Assert.False(IdCardPdfRules.HasExactlyA4PortraitPage(json));
    }

    [Fact]
    public void RejectsMultiplePages()
    {
        var page = Page(A4WidthPoints, A4HeightPoints)[1..^1];
        Assert.False(IdCardPdfRules.HasExactlyA4PortraitPage($"[{page},{page}]"));
    }

    [Fact]
    public void RejectsImageMetadataBecauseIDStudioRequiresAPdfMediaBox()
    {
        Assert.False(IdCardPdfRules.HasExactlyA4PortraitPage(
            "[{\"pixelWidth\":2480,\"pixelHeight\":3508,\"orientation\":\"Portrait\"}]"));
    }

    private static string Page(double width, double height) =>
        $"[{{\"widthPoints\":{N(width)},\"heightPoints\":{N(height)},\"orientation\":\"Portrait\",\"images\":[]}}]";

    private static string N(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
