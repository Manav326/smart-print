using System.Security.Cryptography;
using System.Text.Json;
using SmartPrint.Agent;
using Xunit;

namespace SmartPrint.Agent.Tests;

/// <summary>
/// Opt-in integration driven by the settings actually selected by the shop-owner
/// browser workflow. The Windows runner installs the Epson L360 vendor driver and
/// uses a private file port, proving Epson PrintTicket/DEVMODE validation and
/// Windows spooler output without pretending that a USB printer made physical paper.
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

    private static string ShopOwnerManifestPath =>
        Environment.GetEnvironmentVariable("SMARTPRINT_EPSON_L360_PRINT_MANIFEST")
        ?? throw new InvalidOperationException("SMARTPRINT_EPSON_L360_PRINT_MANIFEST is required.");

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

        var media = ReadPhotoMediaNames(root);
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

    [Fact]
    public void ShopOwnerAssignedTwoUpAndFourUpJobsAreAcceptedByEpsonL360Driver()
    {
        if (!IsEnabled)
            return;

        using var manifest = ReadAndValidateShopOwnerManifest();
        var jobs = manifest.RootElement.GetProperty("documentJobs").EnumerateArray().ToArray();
        Assert.Equal(2, jobs.Length);
        Assert.Contains(jobs, job => job.GetProperty("pagesPerSheet").GetInt32() == 2);
        Assert.Contains(jobs, job => job.GetProperty("pagesPerSheet").GetInt32() == 4);

        foreach (var job in jobs)
        {
            var pagesPerSheet = job.GetProperty("pagesPerSheet").GetInt32();
            var pageCount = job.GetProperty("pageCount").GetInt32();
            var copies = job.GetProperty("copies").GetInt32();
            var fileName = GetRequiredString(job, "fileName");
            var bytes = CreatePdf(
                $"SHOP OWNER {GetRequiredString(job, "jobNumber")} EPSON L360 {pagesPerSheet}-UP",
                pageCount);

            try
            {
                var entry = CreateEntry(
                    bytes,
                    fileName,
                    GetRequiredString(job, "mediaType"),
                    pageCount,
                    copies,
                    GetRequiredString(job, "colourMode"),
                    GetRequiredString(job, "paperSize"),
                    GetRequiredString(job, "orientation"),
                    job.GetProperty("isDuplex").GetBoolean(),
                    printKind: "DOCUMENT",
                    pagesPerSheet: pagesPerSheet);

                SubmitAndVerifySpoolerOutput(
                    entry,
                    bytes,
                    GetRequiredString(job, "jobNumber") + $" ({pagesPerSheet}-up)");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    [Fact]
    public void ShopOwnerAssignedPhotoDimensionsAndStockAreAcceptedByEpsonL360Driver()
    {
        if (!IsEnabled)
            return;

        using var manifest = ReadAndValidateShopOwnerManifest();
        var jobs = manifest.RootElement.GetProperty("photoJobs").EnumerateArray().ToArray();
        Assert.Equal(4, jobs.Length);

        foreach (var job in jobs)
        {
            var jobNumber = GetRequiredString(job, "jobNumber");
            var photoSizeCode = GetRequiredString(job, "photoSizeCode");
            var photoPaperFinish = GetRequiredString(job, "photoPaperFinish");
            var photoOutputLayout = GetRequiredString(job, "photoOutputLayout");
            var mediaType = GetRequiredString(job, "mediaType");
            var pageCount = job.GetProperty("pageCount").GetInt32();
            var copies = job.GetProperty("copies").GetInt32();
            var bytes = photoOutputLayout switch
            {
                "A4_SHEET" => CreateA4PhotoSheetPdf(photoSizeCode, photoCount: job.GetProperty("photoCount").GetInt32()),
                "PHOTO_PAPER" when photoSizeCode == "4x6" =>
                    CreatePhotoJpeg(width: 600, height: 900),
                _ => throw new InvalidOperationException(
                    $"Shop owner requested an unexpected Epson photo layout '{photoOutputLayout}' for {photoSizeCode}.")
            };

            try
            {
                var entry = CreateEntry(
                    bytes,
                    GetRequiredString(job, "fileName"),
                    mediaType,
                    pageCount,
                    copies,
                    "Colour",
                    GetRequiredString(job, "paperSize"),
                    "PORTRAIT",
                    isDuplex: false,
                    printKind: "PHOTO",
                    pagesPerSheet: 1,
                    photoPaperFinish: photoPaperFinish,
                    photoOutputLayout: photoOutputLayout,
                    photoSizeCode: photoSizeCode);

                SubmitAndVerifySpoolerOutput(
                    entry,
                    bytes,
                    $"{jobNumber}: {photoSizeCode} {photoPaperFinish} {photoOutputLayout}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static JsonDocument ReadAndValidateShopOwnerManifest()
    {
        Assert.True(OperatingSystem.IsWindows(), "The Epson L360 driver test requires Windows.");
        Assert.True(File.Exists(ShopOwnerManifestPath),
            $"The shop-owner browser test did not create its print manifest at '{ShopOwnerManifestPath}'.");

        var manifest = JsonDocument.Parse(File.ReadAllText(ShopOwnerManifestPath));
        try
        {
            var root = manifest.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("SHOP_OWNER_BROWSER_WORKFLOW", GetRequiredString(root, "source"));
            Assert.False(root.GetProperty("physicalPrinterConnected").GetBoolean(),
                "The CI runner is not a physical USB-connected printer; the file port is a driver/spooler test only.");
            Assert.Equal(DriverName, GetRequiredString(root, "driverName"));
            Assert.Equal(QueueName, GetRequiredString(root, "queueName"));

            var printerId = GetRequiredString(root, "printerDeviceId");
            var printerName = GetRequiredString(root, "printerName");
            var documentJobs = root.GetProperty("documentJobs").EnumerateArray().ToArray();
            var photoJobs = root.GetProperty("photoJobs").EnumerateArray().ToArray();
            var expectedJobIds = documentJobs.Concat(photoJobs)
                .Select(job => GetRequiredString(job, "id"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var assignments = root.GetProperty("assignmentEvents").EnumerateArray().ToArray();
            var dispatches = root.GetProperty("dispatchEvents").EnumerateArray().ToArray();

            Assert.Equal(expectedJobIds.Count, assignments.Length);
            Assert.Equal(expectedJobIds.Count, dispatches.Length);
            foreach (var assignment in assignments)
            {
                Assert.Equal(printerId, GetRequiredString(assignment, "printerDeviceId"));
                Assert.Equal(printerName, GetRequiredString(assignment, "printerName"));
                Assert.Equal(QueueName, GetRequiredString(assignment, "queueName"));
            }

            var dispatchedJobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dispatch in dispatches)
            {
                Assert.Equal(printerId, GetRequiredString(dispatch, "printerDeviceId"));
                Assert.Equal(QueueName, GetRequiredString(dispatch, "queueName"));
                Assert.True(
                    dispatchedJobIds.Add(GetRequiredString(dispatch, "jobId")),
                    "The shop owner must not dispatch the same Epson job twice.");
            }
            Assert.True(expectedJobIds.SetEquals(dispatchedJobIds),
                "Every Agent print must have an exact shop-owner assignment and dispatch event.");

            return manifest;
        }
        catch
        {
            manifest.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<string> ReadPhotoMediaNames(JsonElement root)
    {
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

        return media;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"Shop-owner Epson manifest property '{propertyName}' is missing.");
        return value.GetString()!;
    }

    private static AgentLocalQueueEntry CreateEntry(
        byte[] bytes,
        string fileName,
        string mediaType,
        int pageCount,
        int copies,
        string colourMode,
        string paperSize,
        string orientation,
        bool isDuplex,
        string printKind,
        int pagesPerSheet,
        string photoPaperFinish = "PLAIN",
        string? photoOutputLayout = null,
        string? photoSizeCode = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentLocalQueueEntry(
            Guid.NewGuid(),
            "SP-EPSON-L360-SHOP-OWNER-QA",
            Guid.NewGuid(),
            "Epson L360 Series",
            QueueName,
            fileName,
            mediaType,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength,
            pageCount,
            copies,
            colourMode,
            paperSize,
            isDuplex,
            "INR",
            100,
            "SmartPrint-Epson-L360-Shop-Owner-Integration",
            "ACKNOWLEDGED",
            now,
            now.AddMinutes(5),
            Orientation: orientation,
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(SpoolerOutputPath))!);
        if (File.Exists(SpoolerOutputPath))
            File.Delete(SpoolerOutputPath);

        var receipt = new WindowsPrintExecutor().Print(entry, bytes, CancellationToken.None);
        Assert.True(receipt.JobId > 0,
            $"The Epson L360 driver did not return a Windows spooler job ID for {scenario}.");

        var deadline = DateTime.UtcNow.AddSeconds(45);
        long previousLength = -1;
        var stableSamples = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(SpoolerOutputPath))
            {
                var info = new FileInfo(SpoolerOutputPath);
                if (info.Length > 128 && info.Length == previousLength)
                    stableSamples++;
                else
                    stableSamples = 0;
                previousLength = info.Length;

                if (stableSamples >= 3)
                {
                    var safeName = string.Concat(scenario.Select(character =>
                        char.IsLetterOrDigit(character) ? character : '-'));
                    var acceptedOutput = Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(SpoolerOutputPath))!,
                        safeName + "-accepted.prn");
                    File.Copy(SpoolerOutputPath, acceptedOutput, overwrite: true);
                    Console.WriteLine(
                        $"Epson L360 accepted {scenario}: JobId={receipt.JobId}; driver spooler bytes={info.Length}; evidence={acceptedOutput}.");
                    return;
                }
            }

            Thread.Sleep(250);
        }

        Assert.True(false,
            $"Epson L360 accepted StartDoc for {scenario} (JobId={receipt.JobId}) but the isolated spooler file port did not reach a stable output file.");
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

    private static byte[] CreateA4PhotoSheetPdf(string photoSizeCode, int photoCount)
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
            var columns = Math.Max(1, Math.Min(photoCount, (int)Math.Floor((499f + gap) / (widthPoints + gap))));
            for (var index = 0; index < photoCount; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var left = 48f + column * (widthPoints + gap);
                var top = 100f + row * (heightPoints + gap);
                canvas.DrawRect(new SkiaSharp.SKRect(left, top, left + widthPoints, top + heightPoints), border);
                canvas.DrawText($"{photoSizeCode} mm", left, top + heightPoints + 14f,
                    SkiaSharp.SKTextAlign.Left, font, labelPaint);
            }

            pdf.EndPage();
            pdf.Close();
        }

        return source.ToArray();
    }

    private static byte[] CreatePhotoJpeg(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        using (var paint = new SkiaSharp.SKPaint
        {
            Color = SkiaSharp.SKColors.Blue,
            IsAntialias = true
        })
        {
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) * 0.36f, paint);
        }

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 92);
        return encoded.ToArray();
    }
}
