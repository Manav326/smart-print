using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PhotoStudioEditorSettingsRulesTests
{
    [Fact]
    public void AcceptsAndPreservesAValidEditorSettingsObject()
    {
        const string json = """{"sizeId":"35x45","zoom":1.4,"focusX":46,"focusY":58,"textLayers":[]}""";

        Assert.True(PhotoStudioEditorSettingsRules.TryNormalizeJson(json, out var normalized));
        Assert.Equal(json, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsMissingSettingsAsNoSavedOverride(string? json)
    {
        Assert.True(PhotoStudioEditorSettingsRules.TryNormalizeJson(json, out var normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    public void RejectsInvalidOrNonObjectSettings(string json)
    {
        Assert.False(PhotoStudioEditorSettingsRules.TryNormalizeJson(json, out _));
    }

    [Theory]
    [InlineData("""{"photoName":42}""")]
    [InlineData("""{"dateOfBirth":[]}""")]
    [InlineData("""{"adjustments":"invalid"}""")]
    [InlineData("""{"textLayers":"invalid"}""")]
    [InlineData("""{"textLayers":[null]}""")]
    [InlineData("""{"textLayers":[{}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}]}""")]
    public void RejectsMalformedPhotoStudioFieldShapes(string json)
    {
        Assert.False(PhotoStudioEditorSettingsRules.TryNormalizeJson(json, out _));
    }

    [Fact]
    public void RejectsSettingsLargerThanTheBound()
    {
        var json = "{\"text\":\"" + new string('x', PhotoStudioEditorSettingsRules.MaximumJsonLength) + "\"}";
        Assert.False(PhotoStudioEditorSettingsRules.TryNormalizeJson(json, out _));
    }

    [Fact]
    public void RejectsExcessivelyNestedSettings()
    {
        var json = "{\"a\":" + new string('[', 13) + "0" + new string(']', 13) + "}";
        Assert.False(PhotoStudioEditorSettingsRules.TryNormalizeJson(json, out _));
    }
}
