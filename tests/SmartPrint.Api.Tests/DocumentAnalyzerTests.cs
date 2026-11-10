using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class DocumentAnalyzerTests
{
    [Fact]
    public void AnalysesFullyDecodedPng()
    {
        using var image = new Image<Rgba32>(640, 480);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());

        var result = DocumentAnalyzer.Analyse(stream.ToArray(), ".png");

        Assert.Equal(1, result.PageCount);
        Assert.Equal("image/png", result.MediaType);
        Assert.Equal(".png", result.Extension);
        using var sizes = JsonDocument.Parse(result.PageSizesJson);
        Assert.Equal(640, sizes.RootElement[0].GetProperty("pixelWidth").GetInt32());
        Assert.Equal(480, sizes.RootElement[0].GetProperty("pixelHeight").GetInt32());
    }

    [Fact]
    public void RejectsTruncatedPngEvenWhenItsHeaderIsValid()
    {
        using var image = new Image<Rgba32>(4, 4);
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        var complete = stream.ToArray();
        var truncated = complete[..^8];

        Assert.Throws<DocumentValidationException>(() =>
            DocumentAnalyzer.Analyse(truncated, ".png"));
    }

    [Fact]
    public void RejectsPngWithInvalidDimensions()
    {
        var bytes = new byte[24];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);

        Assert.Throws<DocumentValidationException>(() => DocumentAnalyzer.Analyse(bytes, ".png"));
    }

    [Fact]
    public void RejectsPdfWithInvalidSignature()
    {
        Assert.Throws<DocumentValidationException>(() =>
            DocumentAnalyzer.Analyse("not a pdf"u8.ToArray(), ".pdf"));
    }

    [Fact]
    public void RejectsJpegWithInvalidSignature()
    {
        Assert.Throws<DocumentValidationException>(() =>
            DocumentAnalyzer.Analyse("not a jpeg"u8.ToArray(), ".jpg"));
    }
}
