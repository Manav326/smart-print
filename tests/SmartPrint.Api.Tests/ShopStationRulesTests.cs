using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class ShopStationRulesTests
{
    [Theory]
    [InlineData("https://print.example.com", "https://print.example.com")]
    [InlineData("https://print.example.com/", "https://print.example.com")]
    [InlineData("https://print.example.com///", "https://print.example.com")]
    public void AcceptsCanonicalHttpsBaseUrls(string supplied, string expected)
    {
        Assert.True(ShopStationRules.TryNormalizePublicPortalBaseUrl(supplied, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://print.example.com")]
    [InlineData("https://print.example.com/path")]
    [InlineData("https://print.example.com/?query=1")]
    [InlineData("https://user:pass@print.example.com")]
    [InlineData("not-a-url")]
    public void RejectsNonCanonicalOrNonHttpsPublicBaseUrls(string supplied)
    {
        Assert.False(ShopStationRules.TryNormalizePublicPortalBaseUrl(supplied, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData(" Main Counter ", true, "Main Counter")]
    [InlineData("", false, "")]
    [InlineData("   ", false, "")]
    public void NormalizesStationNames(string supplied, bool expectedValid, string expected)
    {
        Assert.Equal(expectedValid, ShopStationRules.TryNormalizeDisplayName(supplied, out var name));
        Assert.Equal(expected, name);
    }

    [Fact]
    public void EnforcesStationNameLength()
    {
        Assert.False(ShopStationRules.TryNormalizeDisplayName(new string('x', 121), out _));
        Assert.True(ShopStationRules.TryNormalizeDisplayName(new string('x', 120), out _));
    }

    [Theory]
    [InlineData("PHYSICAL", true)]
    [InlineData("PDF_EXPORT", true)]
    [InlineData("VIRTUAL", false)]
    [InlineData("DOCUMENT", false)]
    [InlineData(null, false)]
    public void AllowsOnlyPhysicalOrPdfExportOutputModes(string? mode, bool expected) =>
        Assert.Equal(expected, ShopStationRules.IsSupportedOutputMode(mode));
}
