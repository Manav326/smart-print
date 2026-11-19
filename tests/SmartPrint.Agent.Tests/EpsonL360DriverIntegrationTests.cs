using System.Security.Cryptography;
using System.Text.Json;
using SmartPrint.Agent;
using Xunit;

namespace SmartPrint.Agent.Tests;

/// <summary>
/// Opt-in integration against the genuine Epson L360 v2.60 Windows driver.
/// CI creates an isolated queue backed by a file port; this exercises Epson's
/// PrintTicket/DEVMODE validation and spooler submission without claiming a
/// USB-connected printer physically produced paper.
/// </summary>
public sealed class EpsonL360DriverIntegrationTests
{
    private static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("SMARTPRINT_RUN_EPSON_L360_DRIVER_INTEGRATION"),
            "1",
            StringComparison.Ordinal);

    private static string QueueName =>
        Environment.GetEnvironmentVariable("SMARTPRINT_EPSON_L360_QUEUE")
        ?? throw new InvalidOperationException("SMARTPRINT_EPSON_L360_QUEUE is required.");

    private static string DriverName =>
        Environment.GetEnvironmentVariable("SMARTPRINT_EPSON_L360_DRIVER_NAME")
        ?? throw new InvalidOperationException("SMARTPRINT_EPSON_L360_DRIVER_NAME is required.");

    private static string SpoolerOutputPath =>
        Environment.GetEnvironmentVariable("SMARTPRINT_EPSON_L360_SPOOLER_OUTPUT")
        ?? throw new InvalidOperationException("SMARTPRINT_EPSON_L360_SPOOLER_OUTPUT is required.");

    [Fact]
    public void EpsonL360CapabilitySnapshotAdvertisesNupAndPhotoMediaForShopOwnerDetails()
    {
        if (!IsEnabled)
            return;

        Assert.True(OperatingSystem.IsWindows(), "The Epson L360 driver test requires Windows.");
        Assert.True(DriverName.Contains("EPSON", StringComparison.OrdinalIgnoreCase));
        Assert.True(DriverName.Contains("L360", StringComparison.OrdinalIgnoreCase));

        var json = WindowsPrinterCapabilityDiscovery.DiscoverJson(QueueName, DriverName);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("WINDOWS_DRIVER", root.GetProperty("source").GetString());
        Assert.Equal(DriverName, root.GetProperty("driverName").GetString());

        var nup = root.GetProperty("pagesPerSheet").EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.Number)
            .Select(value => value.GetInt32())
            .ToHashSet();
        Assert.Contains(2, nup);
        Assert.Contains(4, nup);

        var media = new List<string>();
        if (root.TryGetProperty("mediaTypes", out var legacyMedia) &&
            legacyMedia.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in legacyMedia.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(name.GetString()))
                    media.Add(name.GetString()!);
            }
        }

        if (root.TryGetProperty("pageMediaTypes", out var schemaMedia) &&
            schemaMedia.ValueKind == JsonValueKind.Array)
        {
            media.AddRange(schemaMedia.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        Assert.True(media.Any(value => value.Contains("gloss", StringComparison.OrdinalIgnoreCase)),
            "Epson L360 driver did not report an available Glossy stock.");
        Assert.True(media.Any(value => value.Contains("matt", StringComparison.OrdinalIgnoreCase)),
            "Epson L360 driver did not report an available Matte stock.");

        var outputPath = Environment.GetEnvironmentVariable("SMARTPRINT_EPSON_L360_CAPABILITIES_OUTPUT");
        Assert.False(string.IsNullOrWhiteSpace(outputPath),
            "Set SMARTPRINT_EPSON_L360_CAPABILITIES_OUTPUT so the shop-owner browser test uses this exact driver snapshot.");
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath!))!;
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(outputPath!, json);

        Console.WriteLine(
            $"EPSON L360 capability snapshot saved for shop-owner UI. N-up=[{string.Join(",", nup.OrderBy(value => value))}], media=[{string.Join(", ", media.Distinct())}].");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void EpsonL360AcceptsDriverBackedNupAndSpoolerSubmission(int pagesPerSheet)
    {
        if (!IsEnabled)
            return;

        var bytes = CreatePdf($"EPSON L360 {pagesPerSheet}-UP", pageCount: 8);
        var entry = CreateEntry(
            bytes,
            $"epson-l360-{pagesPerSheet}-up.pdf",
            "application/pdf",
            pageCount: 8,
            printKind: "DOCUMENT",
            pagesPerSheet: pagesPerSheet);

        try
        {
            SubmitAndVerifySpoolerOutput(entry, bytes, $"{pagesPerSheet}-up");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    [Theory]
    [InlineData("35x45", "GLOSSY")]
    [InlineData("25x35", "MATTE")]
    [InlineData("2x2", "GLOSSY")]
    public void EpsonL360AcceptsPassportAndIdPhotoDimensionsOnA4PhotoPaper(
        string photoSizeCode,
        string finish)
    {
        if (!IsEnabled)
            return;

        // These are photo dimensions composed onto an A4 sheet. The physical
        // stock presented to the printer is A4 glossy/matte photo paper; the
        // small dimensions are preserved in the prepared PDF layout.
        var bytes = CreateA4PhotoSheetPdf(photoSizeCode);
        var entry = CreateEntry(
            bytes,
            $"epson-l360-{photoSizeCode}-{finish}-a4-photo-sheet.pdf",
            "application/pdf",
            pageCount: 1,
            printKind: "PHOTO",
            pagesPerSheet: 1,
            photoPaperFinish: finish,
            photoOutputLayout: "A4_SHEET",
            photoSizeCode: photoSizeCode);

        try
        {
            SubmitAndVerifySpoolerOutput(entry, bytes, $"{photoSizeCode} on A4 {finish} photo paper");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    [Fact]
    public void EpsonL360AcceptsFourBySixGlossyPhotoPaperAtExactPhysicalDimensions()
    {
        if (!IsEnabled)
            return;

        using var bitmap = new SkiaSharp.SKBitmap(600, 900);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        using (var paint = new SkiaSharp.SKPaint
        {
            Color = SkiaSharp.SKColors.Blue,
            IsAntialias = true
        })
        {
            canvas.DrawCircle(300, 450, 220, paint);
        }

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 92);
        var bytes = encoded.ToArray();
        var entry = CreateEntry(
            bytes,
            "epson-l360-4x6-glossy-photo-paper.jpg",
            "image/jpeg",
            pageCount: 1,
            printKind: "PHOTO",
            pagesPerSheet: 1,
            photoPaperFinish: "GLOSSY",
            photoOutputLayout: "PHOTO_PAPER",
            photoSizeCode: "4x6");

        try
        {
            SubmitAndVerifySpoolerOutput(entry, bytes, "4x6 GLOSSY physical photo paper");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static AgentLocalQueueEntry CreateEntry(
        byte[] bytes,
        string fileName,
        string mediaType,
        int pageCount,
        string printKind,
        int pagesPerSheet,
        string photoPaperFinish = "PLAIN",
        string? photoOutputLayout = null,
        string? photoSizeCode = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentLocalQueueEntry(
            Guid.NewGuid(),
            "SP-EPSON-L360-DRIVER-QA",
            Guid.NewGuid(),
            "Epson L360 Series",
            QueueName,
            fileName,
            mediaType,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength,
            pageCount,
            1,
            "Colour",
            "A4",
            false,
            "INR",
            100,
            "SmartPrint-Epson-L360-Driver-Integration",
            "ACKNOWLEDGED",
            now,
            now.AddMinutes(5),
            Orientation: "PORTRAIT",
            PrintKind: printKind,
            PagesPerSheet: pagesPerSheet,
            PhotoPaperFinish: photoPaperFinish,
            PhotoOutputLayout: photoOutputLayout,
            PhotoSizeCode: photoSizeCode);
    }

    private static void SubmitAndVerifySpoolerOutput(
        AgentLocalQueueEntry entry,
        byte[] bytes,
        string scenario)
    {
        Assert.True(OperatingSystem.IsWindows(), "The Epson L360 print test requires Windows.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(SpoolerOutputPath))!);
        if (File.Exists(SpoolerOutputPath))
            File.Delete(SpoolerOutputPath);

        var receipt = new WindowsPrintExecutor().Print(entry, bytes, CancellationToken.None);
        Assert.True(receipt.JobId > 0,
            $"The Epson L360 driver did not return a Windows spooler job ID for {scenario}.");

        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(SpoolerOutputPath))
            {
                var info = new FileInfo(SpoolerOutputPath);
                if (info.Length > 128)
                {
                    Console.WriteLine(
                        $"Epson L360 accepted {scenario}: JobId={receipt.JobId}; spooler file bytes={info.Length}; output={SpoolerOutputPath}.");
                    return;
                }
            }

            Thread.Sleep(250);
        }

        Assert.True(false,
            $"Epson L360 accepted the StartDoc call for {scenario} (JobId={receipt.JobId}) but the isolated spooler file port did not receive output.");
    }

    private static byte[] CreatePdf(string label, int pageCount)
    {
        using var source = new MemoryStream();
        using (var pdf = SkiaSharp.SKDocument.CreatePdf(source))
        {
            using var typeface = SkiaSharp.SKTypeface.FromFamilyName("Arial", SkiaSharp.SKFontStyle.Normal);
            using var font = new SkiaSharp.SKFont(typeface, 20);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                var canvas = pdf.BeginPage(595.276f, 841.89f);
                canvas.DrawText($"{label} · SOURCE PAGE {pageNumber}", 48, 72,
                    SkiaSharp.SKTextAlign.Left, font, paint);
                pdf.EndPage();
            }
            pdf.Close();
        }

        return source.ToArray();
    }

    private static byte[] CreateA4PhotoSheetPdf(string photoSizeCode)
    {
        var dimensions = photoSizeCode switch
        {
            "35x45" => (WidthMm: 35d, HeightMm: 45d),
            "25x35" => (WidthMm: 25d, HeightMm: 35d),
            "2x2" => (WidthMm: 50.8d, HeightMm: 50.8d),
            _ => throw new ArgumentOutOfRangeException(nameof(photoSizeCode))
        };

        using var source = new MemoryStream();
        using (var pdf = SkiaSharp.SKDocument.CreatePdf(source))
        {
            var canvas = pdf.BeginPage(595.276f, 841.89f);
            using var border = new SkiaSharp.SKPaint
            {
                Color = SkiaSharp.SKColors.Black,
                Style = SkiaSharp.SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };
            using var labelPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            using var font = new SkiaSharp.SKFont(
                SkiaSharp.SKTypeface.FromFamilyName("Arial", SkiaSharp.SKFontStyle.Normal), 10);

            var widthPoints = (float)(dimensions.WidthMm * 72d / 25.4d);
            var heightPoints = (float)(dimensions.HeightMm * 72d / 25.4d);
            var gap = 12f;
            for (var row = 0; row < 3; row++)
            {
                var left = 48f + row * (widthPoints + gap);
                var top = 100f;
                canvas.DrawRect(new SkiaSharp.SKRect(left, top, left + widthPoints, top + heightPoints), border);
                canvas.DrawText($"{photoSizeCode} mm", left, top + heightPoints + 14f,
                    SkiaSharp.SKTextAlign.Left, font, labelPaint);
            }

            pdf.EndPage();
            pdf.Close();
        }

        return source.ToArray();
    }
}
