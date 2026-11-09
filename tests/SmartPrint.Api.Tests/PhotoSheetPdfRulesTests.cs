using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PhotoSheetPdfRulesTests
{
    private const double A4WidthPoints = 210d * 72d / 25.4d;
    private const double A4HeightPoints = 297d * 72d / 25.4d;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(100)]
    public void AcceptsExactNumberOfPortraitA4PagesWithOneFullPageRaster(int pageCount)
    {
        var json = Pages(pageCount, A4WidthPoints, A4HeightPoints);

        Assert.True(PhotoSheetPdfRules.HasExactlyA4PortraitPages(json, pageCount));
    }

    [Fact]
    public void RejectsPageCountMismatch()
    {
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            Pages(2, A4WidthPoints, A4HeightPoints), 3));
    }

    [Theory]
    [InlineData(595.28, 792.00)]
    [InlineData(841.89, 595.28)]
    [InlineData(500.00, 700.00)]
    public void RejectsLetterLandscapeOrWrongPageBoxes(double width, double height)
    {
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            Pages(1, width, height), 1));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[\"invalid\"]")]
    [InlineData("[{}]")]
    [InlineData("[{\"widthPoints\":\"595\",\"heightPoints\":842}]")]
    [InlineData("not-json")]
    public void RejectsMalformedOrUnexpectedPageMetadata(string json)
    {
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(json, 1));
    }

    [Fact]
    public void RejectsPhotoPageWithoutExactlyOneRasterImage()
    {
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            PagesWithImages(A4WidthPoints, A4HeightPoints, "[]"), 1));
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            PagesWithImages(A4WidthPoints, A4HeightPoints,
                "[" + FullPageImage(A4WidthPoints, A4HeightPoints) + "," +
                FullPageImage(A4WidthPoints, A4HeightPoints) + "]"), 1));
    }

    [Theory]
    [InlineData(2479, 3508, 0, 0, 0)]
    [InlineData(2480, 3507, 0, 0, 0)]
    [InlineData(2480, 3508, 2, 0, 0)]
    [InlineData(2480, 3508, 0, 2, 0)]
    [InlineData(2480, 3508, 0, 0, 1)]
    public void RejectsRasterWithWrongResolutionPlacementOrRotation(
        int pixelWidth, int pixelHeight, double left, double bottom, double rotation)
    {
        var image = FullPageImage(A4WidthPoints, A4HeightPoints,
            pixelWidth, pixelHeight, left, bottom, rotation, false);
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            PagesWithImages(A4WidthPoints, A4HeightPoints, "[" + image + "]"), 1));
    }

    [Fact]
    public void RejectsImageMaskAndRasterThatDoesNotCoverTheWholePage()
    {
        var mask = FullPageImage(A4WidthPoints, A4HeightPoints, 2480, 3508, 0, 0, 0, true);
        var inset = FullPageImage(A4WidthPoints - 10, A4HeightPoints - 10,
            2480, 3508, 5, 5, 0, false);

        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            PagesWithImages(A4WidthPoints, A4HeightPoints, "[" + mask + "]"), 1));
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            PagesWithImages(A4WidthPoints, A4HeightPoints, "[" + inset + "]"), 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void RejectsUnsupportedExpectedPageCount(int pageCount)
    {
        Assert.False(PhotoSheetPdfRules.HasExactlyA4PortraitPages(
            Pages(1, A4WidthPoints, A4HeightPoints), pageCount));
    }

    private static string Pages(int count, double width, double height)
    {
        var page = Page(width, height, "[" + FullPageImage(width, height) + "]");
        return "[" + string.Join(",", Enumerable.Repeat(page, count)) + "]";
    }

    private static string PagesWithImages(double width, double height, string images) =>
        "[" + Page(width, height, images) + "]";

    private static string Page(double width, double height, string images) =>
        $"{{\"widthPoints\":{N(width)},\"heightPoints\":{N(height)},\"orientation\":\"Portrait\",\"images\":{images}}}";

    private static string FullPageImage(
        double right,
        double top,
        int pixelWidth = 2480,
        int pixelHeight = 3508,
        double left = 0,
        double bottom = 0,
        double rotation = 0,
        bool isImageMask = false) =>
        $"{{\"widthInSamples\":{pixelWidth},\"heightInSamples\":{pixelHeight}," +
        $"\"leftPoints\":{N(left)},\"bottomPoints\":{N(bottom)}," +
        $"\"rightPoints\":{N(right)},\"topPoints\":{N(top)}," +
        $"\"rotationDegrees\":{N(rotation)},\"isImageMask\":{(isImageMask ? "true" : "false")}}}";

    private static string N(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
