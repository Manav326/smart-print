using System.Security.Cryptography;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class WindowsPrintExecutorTests
{
    [Fact]
    public void RejectsUnsupportedDocumentBeforeOpeningAnyPrinter()
    {
        var bytes = "plain text"u8.ToArray();
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-TEST-PRINT", Guid.NewGuid(), "Test printer", "Missing Windows queue",
            "test.txt", "text/plain", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength, 1, 1, "Mono", "A4", false, "INR", 100, "unused", "ACKNOWLEDGED",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3));

        var exception = Assert.Throws<PrintExecutionBlockedException>(() =>
            new WindowsPrintExecutor().Print(entry, bytes, CancellationToken.None));

        Assert.Equal("DOCUMENT_TYPE_UNSUPPORTED", exception.ReasonCode);
    }

    [Theory]
    [InlineData(0x00000020u, "OFFLINE")]
    [InlineData(0x00000040u, "PAPEROUT")]
    [InlineData(0x00000001u, "PAUSED")]
    [InlineData(0x00000002u, "ERROR")]
    [InlineData(0x00000400u, "USERINTERVENTION")]
    [InlineData(0x00000010u, "PRINTING")]
    [InlineData(0x00000008u, "SPOOLING")]
    [InlineData(0x00000080u, "PRINTED")]
    [InlineData(0u, "QUEUED")]
    public void ResolvesWindowsSpoolerStatusFlags(uint flags, string expected)
    {
        Assert.Equal(expected, WindowsPrintExecutor.ResolveSpoolerStatus(flags));
    }

    [Theory]
    [InlineData(0x00000020u | 0x00000010u, "OFFLINE")]
    [InlineData(0x00000040u | 0x00000010u, "PAPEROUT")]
    [InlineData(0x00000001u | 0x00000008u, "PAUSED")]
    [InlineData(0x00000400u | 0x00000010u, "USERINTERVENTION")]
    public void PrinterFaultsTakePriorityOverActiveQueueFlags(uint flags, string expected)
    {
        Assert.Equal(expected, WindowsPrintExecutor.ResolveSpoolerStatus(flags));
    }

    [Fact]
    public void RejectsChangedBytesBeforePrinterAccess()
    {
        var bytes = "tampered document"u8.ToArray();
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-TEST-HASH", Guid.NewGuid(), "Test printer", "Missing Windows queue",
            "test.pdf", "application/pdf", new string('0', 64), bytes.LongLength, 1, 1, "Mono", "A4",
            false, "INR", 100, "unused", "ACKNOWLEDGED", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3));

        var exception = Assert.Throws<PrintExecutionBlockedException>(() =>
            new WindowsPrintExecutor().Print(entry, bytes, CancellationToken.None));

        Assert.Equal("LOCAL_DOCUMENT_INTEGRITY_MISMATCH", exception.ReasonCode);
    }
}


public sealed class WindowsPrintToPdfSpoolerIntegrationTests
{
    [Theory]
    [InlineData("Microsoft Print to PDF")]
    [InlineData("SmartPrint CI Secondary Queue")]
    public async Task SubmitsRealGdiJobThroughTheSelectedWindowsQueueAndVerifiesDriverOutput(string queueName)
    {
        // The workflow opts in only after proving the Microsoft queue and driver exist.
        // Local unit-test runs remain hardware-independent unless explicitly opted in.
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
            return;

        Assert.True(OperatingSystem.IsWindows(), "The Windows spooler integration test requires a Windows runner.");
        if (queueName == "SmartPrint CI Secondary Queue" &&
            !string.Equals(
                Environment.GetEnvironmentVariable("SMARTPRINT_RUN_MULTI_QUEUE_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
            return;

        using var bitmap = new SkiaSharp.SKBitmap(180, 240);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        using (var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(32, 96, 180), IsAntialias = true })
            canvas.DrawRect(new SkiaSharp.SKRect(18, 18, 162, 222), paint);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "smartprint-real-spooler-" + Guid.NewGuid().ToString("N"));
        var outputName = queueName.Replace(' ', '-').ToLowerInvariant();
        var outputPath = Path.Combine(outputDirectory, $"{outputName}-spooler-output.pdf");
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-REAL-SPOOLER-TEST", Guid.NewGuid(), queueName, queueName,
            "real-spooler-test.png", "image/png",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            1, 1, "Colour", "A4", false, "INR", 100, "SmartPrint-Real-Spooler-Integration",
            "ACKNOWLEDGED", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3),
            OutputMode: PrinterOutputModeRules.Physical);

        try
        {
            // This is the same production orchestration used by Worker: Microsoft
            // Print to PDF first, SmartPrint fallback only for a pre-submission block.
            var result = await new WindowsPrintExecutor().ExportPdfUsingWindowsPrinterOrFallbackAsync(
                entry with { OutputMode = PrinterOutputModeRules.PdfExport },
                bytes,
                outputPath,
                outputDirectory,
                CancellationToken.None);

            Assert.False(result.UsedFallback, "The real Microsoft driver should handle this successful spooler test.");
            Assert.Equal("MICROSOFT_PRINT_TO_PDF", result.Method);
            Assert.True(result.SpoolerJobId is > 0, "Windows did not return a spooler job identifier.");
            var receipt = result.Receipt;
            Assert.Equal(outputPath, receipt.OutputPath);

            var deadline = DateTime.UtcNow.AddSeconds(45);
            byte[]? outputBytes = null;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(outputPath))
                {
                    try
                    {
                        var candidate = await File.ReadAllBytesAsync(outputPath);
                        if (candidate.Length > 1000 &&
                            candidate.AsSpan(0, 5).SequenceEqual("%PDF-"u8) &&
                            System.Text.Encoding.ASCII.GetString(candidate).Contains("%%EOF", StringComparison.Ordinal))
                        {
                            outputBytes = candidate;
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        // The Windows print processor may still be finalizing the output file.
                    }
                }

                await Task.Delay(500);
            }

            Assert.NotNull(outputBytes);
            Assert.True(outputBytes!.Length > 1000, "The Windows printer driver produced an empty or truncated PDF.");
            Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(outputBytes, 0, 5));
            Assert.Contains("%%EOF", System.Text.Encoding.ASCII.GetString(outputBytes), StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }
}

public sealed class WindowsPrintToPdfFallbackTests
{
    [Fact]
    public async Task FallsBackToSmartPrintPdfOnlyWhenWindowsRejectsBeforeSpoolerSubmission()
    {
        using var bitmap = new SkiaSharp.SKBitmap(96, 128);
        bitmap.Erase(SkiaSharp.SKColors.White);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "smartprint-pdf-fallback-" + Guid.NewGuid().ToString("N"));
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-TEST-PDF-FALLBACK", Guid.NewGuid(), "Microsoft Print to PDF",
            "SmartPrint Missing Test Queue", "fallback.png", "image/png",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            1, 1, "Mono", "A4", false, "INR", 100, "unused", "ACKNOWLEDGED",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3),
            OutputMode: PrinterOutputModeRules.PdfExport);
        var executor = new WindowsPrintExecutor();
        var outputPath = executor.GetPdfExportPath(entry, directory);

        try
        {
            var result = await executor.ExportPdfUsingWindowsPrinterOrFallbackAsync(
                entry, bytes, outputPath, directory, CancellationToken.None);

            Assert.True(result.UsedFallback);
            Assert.StartsWith("SMARTPRINT_FALLBACK:", result.Method, StringComparison.Ordinal);
            Assert.Null(result.SpoolerJobId);
            Assert.True(File.Exists(result.Receipt.OutputPath));
            Assert.Equal(outputPath, result.Receipt.OutputPath);
            Assert.True(result.Receipt.SizeBytes > 0);
            Assert.Equal(64, result.Receipt.Sha256.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class PdfExportExecutorTests
{
    [Fact]
    public void ExportsImageToAccessiblePdfIdempotentlyWithoutWindowsPrinter()
    {
        using var bitmap = new SkiaSharp.SKBitmap(24, 32);
        bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "qrtoprint-pdf-export-" + Guid.NewGuid().ToString("N"));
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-TEST-PDF-EXPORT", Guid.NewGuid(), "Microsoft Print to PDF",
            "Microsoft Print to PDF", "sample.png", "image/png",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            1, 2, "Mono", "A4", false, "INR", 300, "unused", "ACKNOWLEDGED",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3),
            OutputMode: PrinterOutputModeRules.PdfExport);

        try
        {
            var executor = new WindowsPrintExecutor();
            var receipt = executor.ExportPdf(entry, bytes, directory, CancellationToken.None);

            Assert.True(File.Exists(receipt.OutputPath));
            Assert.EndsWith(".pdf", receipt.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.True(receipt.SizeBytes > 0);
            Assert.Equal(64, receipt.Sha256.Length);
            var header = new byte[5];
            using (var stream = File.OpenRead(receipt.OutputPath))
                Assert.Equal(5, stream.Read(header));
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(header));

            var retry = executor.ExportPdf(entry, bytes, directory, CancellationToken.None);
            Assert.Equal(receipt.OutputPath, retry.OutputPath);
            Assert.Equal(receipt.Sha256, retry.Sha256);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }


    [Fact]
    public void DoesNotRasterPaintBrandingWhenAComplexExportNeedsRebuilding()
    {
        using var source = new MemoryStream();
        using (var pdf = SkiaSharp.SKDocument.CreatePdf(source))
        {
            var canvas = pdf.BeginPage(595.276f, 841.89f);
            using var typeface = SkiaSharp.SKTypeface.FromFamilyName("Arial", SkiaSharp.SKFontStyle.Normal);
            using var font = new SkiaSharp.SKFont(typeface, 14);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            canvas.DrawText("ORIGINAL DOCUMENT CONTENT", 48, 72, SkiaSharp.SKTextAlign.Left, font, paint);
            pdf.EndPage();
            pdf.Close();
        }

        var bytes = source.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "smartprint-unbranded-fallback-" + Guid.NewGuid().ToString("N"));
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-UNBRANDED-FALLBACK", Guid.NewGuid(), "PDF Export",
            "PDF Export", "original.pdf", "application/pdf",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            1, 2, "Colour", "A4", false, "INR", 100, "unused", "ACKNOWLEDGED",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3),
            OutputMode: PrinterOutputModeRules.PdfExport,
            PdfBrandingEnabled: true);

        try
        {
            var receipt = new WindowsPrintExecutor().ExportPdf(entry, bytes, directory, CancellationToken.None);
            using var rendered = UglyToad.PdfPig.PdfDocument.Open(receipt.OutputPath);
            Assert.Equal(2, rendered.NumberOfPages);
            Assert.DoesNotContain("SmartPrint", string.Join(" ", rendered.GetPages().Select(page => page.Text)), StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PdfExportPrintsOnlyTheAcceptedPageAfterMultiplePagesAreRejected()
    {
        using var source = new MemoryStream();
        using (var pdf = SkiaSharp.SKDocument.CreatePdf(source))
        {
            // Landscape pages bracket the accepted portrait page. Rejecting pages 1 and 3
            // must leave one portrait output page (source page 2), not either rejected page.
            foreach (var landscape in new[] { true, false, true })
            {
                var width = landscape ? 841.89f : 595.276f;
                var height = landscape ? 595.276f : 841.89f;
                var canvas = pdf.BeginPage(width, height);
                using var typeface = SkiaSharp.SKTypeface.FromFamilyName("Arial", SkiaSharp.SKFontStyle.Normal);
                using var font = new SkiaSharp.SKFont(typeface, 18);
                using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
                canvas.DrawText(landscape ? "REJECTED LANDSCAPE PAGE" : "ACCEPTED PORTRAIT PAGE",
                    48, 72, SkiaSharp.SKTextAlign.Left, font, paint);
                pdf.EndPage();
            }
            pdf.Close();
        }

        var bytes = source.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "smartprint-rejected-pages-" + Guid.NewGuid().ToString("N"));
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-MIXED-REFUND-ACCEPTED-PAGE", Guid.NewGuid(),
            "PDF Export", "PDF Export", "mixed-batch.pdf", "application/pdf",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            3, 1, "Colour", "A4", false, "INR", 3_000, "unused", "ACKNOWLEDGED",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3),
            OutputMode: PrinterOutputModeRules.PdfExport,
            RejectedPageNumbers: [1, 3]);

        try
        {
            var receipt = new WindowsPrintExecutor().ExportPdf(entry, bytes, directory, CancellationToken.None);
            using var rendered = UglyToad.PdfPig.PdfDocument.Open(receipt.OutputPath);
            var outputPage = Assert.Single(rendered.GetPages());
            Assert.InRange(Math.Abs(outputPage.Width - 595.276), 0d, 1d);
            Assert.InRange(Math.Abs(outputPage.Height - 841.89), 0d, 1d);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

public sealed class PdfBrandingOverlayTests
{
    [Fact]
    public void UnbrandedSingleCopyPdfExportPreservesTheOriginalPdfBytes()
    {
        using var source = new MemoryStream();
        using (var pdf = SkiaSharp.SKDocument.CreatePdf(source))
        {
            var canvas = pdf.BeginPage(595.276f, 841.89f);
            using var typeface = SkiaSharp.SKTypeface.FromFamilyName("Arial", SkiaSharp.SKFontStyle.Normal);
            using var font = new SkiaSharp.SKFont(typeface, 14);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            canvas.DrawText("ORIGINAL PDF MUST REMAIN INTACT", 48, 72, SkiaSharp.SKTextAlign.Left, font, paint);
            pdf.EndPage();
            pdf.Close();
        }

        var bytes = source.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "smartprint-unbranded-original-" + Guid.NewGuid().ToString("N"));
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-UNBRANDED-ORIGINAL", Guid.NewGuid(), "PDF Export",
            "PDF Export", "original.pdf", "application/pdf",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            1, 1, "Colour", "A4", false, "INR", 100, "unused", "ACKNOWLEDGED",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3),
            OutputMode: PrinterOutputModeRules.PdfExport,
            PdfBrandingEnabled: false);

        try
        {
            var receipt = new WindowsPrintExecutor().ExportPdf(entry, bytes, directory, CancellationToken.None);
            var saved = File.ReadAllBytes(receipt.OutputPath);
            Assert.Equal(bytes, saved);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AddsVectorFooterWithoutFlatteningSelectableOriginalText()
    {
        using var source = new MemoryStream();
        using (var pdf = SkiaSharp.SKDocument.CreatePdf(source))
        {
            var canvas = pdf.BeginPage(595.276f, 841.89f);
            using var typeface = SkiaSharp.SKTypeface.FromFamilyName("Arial", SkiaSharp.SKFontStyle.Normal);
            using var font = new SkiaSharp.SKFont(typeface, 14);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            canvas.DrawText("SELECTABLE ORIGINAL CONTENT", 48, 72, SkiaSharp.SKTextAlign.Left, font, paint);
            pdf.EndPage();
            pdf.Close();
        }

        var bytes = source.ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "smartprint-vector-branding-" + Guid.NewGuid().ToString("N"));
        var entry = new AgentLocalQueueEntry(
            Guid.NewGuid(), "SP-VECTOR-BRAND-TEST", Guid.NewGuid(), "PDF Export",
            "PDF Export", "original.pdf", "application/pdf",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            1, 1, "Colour", "A4", false, "INR", 100, "unused", "ACKNOWLEDGED",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(3),
            OutputMode: PrinterOutputModeRules.PdfExport,
            PdfBrandingEnabled: true);

        try
        {
            var receipt = new WindowsPrintExecutor().ExportPdf(entry, bytes, directory, CancellationToken.None);
            using var rendered = UglyToad.PdfPig.PdfDocument.Open(receipt.OutputPath);
            var page = Assert.Single(rendered.GetPages());
            Assert.Contains("SELECTABLE ORIGINAL CONTENT", page.Text, StringComparison.Ordinal);
            Assert.Contains("SmartPrint", page.Text, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(page.Width - 595.276), 0d, 1d);
            Assert.InRange(Math.Abs(page.Height - 841.89), 0d, 1d);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SignedAndFormDocumentsAreCopiedWithoutAddingBranding()
    {
        // The preservation decision is covered at the PDFium boundary: a signed or
        // interactive-form PDF must never be modified by adding page objects.
        Assert.False(PdfBrandingOverlay.CanModifyDocument(hasDigitalSignatures: true, hasInteractiveForms: false));
        Assert.False(PdfBrandingOverlay.CanModifyDocument(hasDigitalSignatures: false, hasInteractiveForms: true));
        Assert.True(PdfBrandingOverlay.CanModifyDocument(hasDigitalSignatures: false, hasInteractiveForms: false));
    }
}
}
