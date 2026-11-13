using System.Buffers.Binary;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using SmartPrint.Documents;

namespace SmartPrint.Api.Tests;

public sealed class DocumentAnalyzerTests
{
    [Fact]
    public void AnalysesFullyDecodedPng()
    {
        var png = CreatePng(640, 480);

        var result = DocumentAnalyzer.Analyse(png, ".png");

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
        var complete = CreatePng(4, 4);
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
    public void IdentifiesPngFromContentWhenTheFilenameSaysPdf()
    {
        var result = DocumentAnalyzer.Analyse(CreatePng(8, 6), ".pdf");

        Assert.Equal("image/png", result.MediaType);
        Assert.Equal(".png", result.Extension);
    }

    [Fact]
    public void FullyAnalyzesPdfWithHeaderAtLastAllowedOffset()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        var pdf = builder.Build();
        var bytes = new byte[1023 + pdf.Length];
        Array.Fill(bytes, (byte)' ');
        pdf.CopyTo(bytes, 1023);

        var result = DocumentAnalyzer.Analyse(bytes, ".jpg");

        Assert.Equal(1, result.PageCount);
        Assert.Equal("application/pdf", result.MediaType);
        Assert.Equal(".pdf", result.Extension);
    }

    [Fact]
    public void UsesTheSameFirst1024BytePdfHeaderWindowAsAndroid()
    {
        var accepted = new byte[1028];
        "%PDF-"u8.CopyTo(accepted.AsSpan(1023));
        Assert.Equal(".pdf", DocumentAnalyzer.IdentifyExtension(accepted));

        var rejected = new byte[1029];
        "%PDF-"u8.CopyTo(rejected.AsSpan(1024));
        Assert.Null(DocumentAnalyzer.IdentifyExtension(rejected));
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 6; // RGBA
        WritePngChunk(output, "IHDR", header);

        var rowBytes = checked(width * 4);
        var raw = new byte[checked((rowBytes + 1) * height)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (rowBytes + 1)] = 0; // PNG filter: None
            for (var x = 0; x < width; x++)
            {
                var pixel = y * (rowBytes + 1) + 1 + x * 4;
                raw[pixel] = 20;
                raw[pixel + 1] = 80;
                raw[pixel + 2] = 140;
                raw[pixel + 3] = 255;
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw);
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        var crc = ComputePngCrc(crcInput);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
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
