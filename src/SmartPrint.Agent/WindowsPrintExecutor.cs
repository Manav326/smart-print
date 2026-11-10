using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PDFiumCore;
using SkiaSharp;

namespace SmartPrint.Agent;

public sealed record SpoolerPrintReceipt(int JobId, string DocumentName);
public sealed record PdfExportExecutionResult(
    PdfExportReceipt Receipt,
    bool UsedFallback,
    string Method,
    int? SpoolerJobId);

public sealed class PrintExecutionBlockedException(string reasonCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string ReasonCode { get; } = reasonCode;
}

public sealed class PrintSubmissionUncertainException(string reasonCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string ReasonCode { get; } = reasonCode;
}

public sealed class WindowsPrintExecutor
{
    private const int DmOrientation = 0x00000001;
    private const int DmPaperSize = 0x00000002;
    private const int DmColor = 0x00000800;
    private const int DmDuplex = 0x00001000;
    private const int DcPapers = 2;
    private const int DcDuplex = 7;
    private const int DcOrientation = 17;
    private const int DcColorDevice = 32;
    private const int LogPixelsX = 88;
    private const int LogPixelsY = 90;
    private const int HorzRes = 8;
    private const int VertRes = 10;
    private const int PhysicalWidth = 110;
    private const int PhysicalHeight = 111;
    private const int PhysicalOffsetX = 112;
    private const int PhysicalOffsetY = 113;
    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const uint SrcCopy = 0x00CC0020;
    private const int Halftone = 4;
    private const int MaxRasterDimension = 10_000;
    private const long MaxRasterPixels = 24_000_000;
    private static readonly Lazy<bool> PdfiumInitialized = new(() =>
    {
        fpdfview.FPDF_InitLibrary();
        return true;
    });

    public SpoolerPrintReceipt Print(AgentLocalQueueEntry entry, byte[] bytes, CancellationToken cancellationToken) =>
        PrintCore(entry, bytes, cancellationToken, outputFilePath: null);

    /// <summary>
    /// Submits a real Windows GDI print job to the selected printer queue, directing
    /// file-capable printer drivers to the supplied output path through DOCINFO.lpszOutput.
    /// SmartPrint uses this for its Microsoft Print to PDF primary route and Windows spooler QA.
    /// </summary>
    public SpoolerPrintReceipt PrintToFile(
        AgentLocalQueueEntry entry,
        byte[] bytes,
        string outputFilePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
            throw new ArgumentException("A fully qualified output file path is required.", nameof(outputFilePath));

        var fullOutputPath = Path.GetFullPath(outputFilePath);
        if (File.Exists(fullOutputPath))
            throw new IOException("The requested spooler output path already exists.");

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The output path must include a directory.", nameof(outputFilePath));
        Directory.CreateDirectory(outputDirectory);

        return PrintCore(entry, bytes, cancellationToken, fullOutputPath);
    }

    private SpoolerPrintReceipt PrintCore(
        AgentLocalQueueEntry entry,
        byte[] bytes,
        CancellationToken cancellationToken,
        string? outputFilePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PrintExecutionBlockedException("WINDOWS_REQUIRED", "Physical printing is available only on the Windows Print Agent.");
        if (string.IsNullOrWhiteSpace(entry.QueueName))
            throw new PrintExecutionBlockedException("PRINTER_QUEUE_MISSING", "The assigned Windows printer queue name is missing.");
        if (bytes.LongLength != entry.SizeBytes ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(entry.Sha256)))
            throw new PrintExecutionBlockedException("LOCAL_DOCUMENT_INTEGRITY_MISMATCH", "The local document does not match the verified upload.");

        using var raster = OpenDocument(bytes, entry.MediaType, entry.ColourMode == "Mono");
        if (raster.PageCount is <= 0 or > 500)
            throw new PrintExecutionBlockedException("INVALID_PAGE_COUNT", "The document contains an unsupported number of pages.");
        if (string.Equals(entry.PrintKind, "ID_DOCUMENT", StringComparison.OrdinalIgnoreCase) &&
            (entry.PageCount != 1 || raster.PageCount != 1 || entry.Copies != 1 ||
             !string.Equals(entry.PaperSize, "A4", StringComparison.OrdinalIgnoreCase) ||
             entry.IsDuplex || !string.Equals(entry.Orientation, "PORTRAIT", StringComparison.OrdinalIgnoreCase)))
            throw new PrintExecutionBlockedException(
                "ID_CARD_SETTINGS_INVALID",
                "A prepared ID sheet must be exactly one A4 portrait page, one copy and single-sided.");
        var printablePageIndices = GetPrintablePageIndices(raster.PageCount, entry.RejectedPageNumbers);

        IntPtr printer = IntPtr.Zero;
        IntPtr devMode = IntPtr.Zero;
        IntPtr dc = IntPtr.Zero;
        var docStarted = false;
        try
        {
            if (!OpenPrinterW(entry.QueueName, out printer, IntPtr.Zero))
                throw Blocked("PRINTER_OPEN_FAILED", "Windows could not open the assigned printer queue.");

            devMode = CreateDevMode(printer, entry.QueueName, entry,
                ResolveLandscape(entry.Orientation, raster.FirstPageLandscape));
            dc = CreateDCW("WINSPOOL", entry.QueueName, null, devMode);
            if (dc == IntPtr.Zero)
                throw Blocked("PRINTER_CONTEXT_FAILED", "Windows could not create a print context for this printer.");

            var printableWidth = GetDeviceCaps(dc, HorzRes);
            var printableHeight = GetDeviceCaps(dc, VertRes);
            var dpiX = GetDeviceCaps(dc, LogPixelsX);
            var dpiY = GetDeviceCaps(dc, LogPixelsY);
            var dpi = Math.Clamp(Math.Min(dpiX, dpiY), 150, 300);
            if (printableWidth <= 0 || printableHeight <= 0)
                throw new PrintExecutionBlockedException("PRINTER_PRINTABLE_AREA_INVALID", "The printer driver returned an invalid printable area.");

            var idDocumentPlacement = default(IdDocumentPagePlacement);
            if (string.Equals(entry.PrintKind, "ID_DOCUMENT", StringComparison.OrdinalIgnoreCase))
            {
                using var idDocumentRaster = raster.RenderPage(0, dpi);
                var compatible = IdDocumentPrintPlacementPolicy.TryCreate(
                    dpiX,
                    dpiY,
                    dpi,
                    GetDeviceCaps(dc, PhysicalWidth),
                    GetDeviceCaps(dc, PhysicalHeight),
                    GetDeviceCaps(dc, PhysicalOffsetX),
                    GetDeviceCaps(dc, PhysicalOffsetY),
                    printableWidth,
                    printableHeight,
                    idDocumentRaster.Width,
                    idDocumentRaster.Height,
                    out idDocumentPlacement,
                    out var placementFailure);
                if (!compatible)
                    throw new PrintExecutionBlockedException(
                        "ID_CARD_PHYSICAL_SCALE_UNAVAILABLE",
                        "This printer cannot guarantee the ID card's exact A4 print size. " + placementFailure +
                        " Choose a supported A4 printer; SmartPrint will not shrink the ID card silently.");
            }

            var documentName = string.IsNullOrWhiteSpace(entry.SpoolerJobName)
                ? $"SmartPrint-{entry.JobNumber}-{entry.JobId:N}"
                : entry.SpoolerJobName;
            if (documentName.Length > 180) documentName = documentName[..180];

            var info = new DocInfoW
            {
                Size = Marshal.SizeOf<DocInfoW>(),
                DocumentName = documentName,
                OutputFile = outputFilePath
            };
            var spoolerId = StartDocW(dc, ref info);
            if (spoolerId <= 0) throw Blocked("SPOOLER_START_FAILED", "Windows did not accept the print document.");
            docStarted = true;

            // Copies are expanded here (collated) exactly once; driver-level copies stay at one.
            for (var copy = 0; copy < entry.Copies; copy++)
            {
                foreach (var pageIndex in printablePageIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (StartPage(dc) <= 0)
                        throw Uncertain("SPOOLER_START_PAGE_FAILED", "Windows accepted the job but could not start a page.");

                    using var page = raster.RenderPage(pageIndex, dpi);
                    var drawn = string.Equals(entry.PrintKind, "ID_DOCUMENT", StringComparison.OrdinalIgnoreCase)
                        ? DrawPageAtPhysicalScale(dc, page, idDocumentPlacement)
                        : DrawPage(dc, page, printableWidth, printableHeight);
                    if (drawn is 0 or -1)
                        throw Uncertain("SPOOLER_RENDER_FAILED", "Windows accepted the job but could not render a page.");

                    if (EndPage(dc) <= 0)
                        throw Uncertain("SPOOLER_END_PAGE_FAILED", "Windows accepted the job but could not finish a page.");
                }
            }

            if (EndDoc(dc) <= 0)
                throw Uncertain("SPOOLER_END_DOC_FAILED", "Windows did not confirm completion of spool submission.");

            return new SpoolerPrintReceipt(spoolerId, documentName);
        }
        catch (PrintExecutionBlockedException exception)
        {
            // Once StartDoc succeeds, Windows owns a real spooler job. Even a later
            // rendering/settings exception is no longer safe for automatic PDF fallback.
            if (docStarted)
            {
                if (dc != IntPtr.Zero) _ = AbortDoc(dc);
                throw new PrintSubmissionUncertainException(
                    "SPOOLER_OUTPUT_OUTCOME_UNCERTAIN",
                    "Windows accepted the print job before an error occurred; inspect the spooler output before retrying.",
                    exception);
            }
            throw;
        }
        catch (PrintSubmissionUncertainException)
        {
            if (dc != IntPtr.Zero && docStarted) _ = AbortDoc(dc);
            throw;
        }
        catch (OperationCanceledException exception) when (docStarted)
        {
            if (dc != IntPtr.Zero) _ = AbortDoc(dc);
            throw new PrintSubmissionUncertainException("AGENT_STOPPED_DURING_PRINT", "The agent stopped during spooler submission; inspect the physical output before retrying.", exception);
        }
        catch (Exception exception)
        {
            if (docStarted)
            {
                if (dc != IntPtr.Zero) _ = AbortDoc(dc);
                throw new PrintSubmissionUncertainException("PRINT_SUBMISSION_INTERRUPTED", "The print may have reached Windows; automatic retry is disabled.", exception);
            }
            throw new PrintExecutionBlockedException("PRINT_PREFLIGHT_FAILED", "The printer could not prepare this job.", exception);
        }
        finally
        {
            if (dc != IntPtr.Zero) _ = DeleteDC(dc);
            if (devMode != IntPtr.Zero) Marshal.FreeHGlobal(devMode);
            if (printer != IntPtr.Zero) _ = ClosePrinter(printer);
        }
    }

    /// <summary>
    /// Uses the selected Windows Microsoft Print to PDF queue first. SmartPrint's
    /// own PDF renderer is used only when Windows rejects the job before StartDoc
    /// accepts it. A job accepted by the spooler with an uncertain output is never
    /// duplicated through the fallback renderer.
    /// </summary>
    public async Task<PdfExportExecutionResult> ExportPdfUsingWindowsPrinterOrFallbackAsync(
        AgentLocalQueueEntry entry,
        byte[] bytes,
        string outputPath,
        string exportDirectory,
        CancellationToken cancellationToken)
    {
        if (entry.PdfBrandingEnabled &&
            string.Equals(entry.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            // Only the opt-in branded PDF route uses this renderer so footer placement
            // can be guarded by a source-page blank-margin check before drawing.
            var brandedReceipt = ExportPdf(entry, bytes, exportDirectory, cancellationToken);
            return new PdfExportExecutionResult(brandedReceipt, false, "SMARTPRINT_BRANDED_PDF_RENDERER", null);
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullOutputPath))
        {
            var existing = ReadExistingPdfExport(fullOutputPath);
            return new PdfExportExecutionResult(existing, false, "EXISTING_VERIFIED_OUTPUT", null);
        }

        SpoolerPrintReceipt spoolerReceipt;
        try
        {
            // This is the same PrintToFile -> PrintCore GDI route exercised by the
            // GitHub Windows integration test, but now it is the production path.
            spoolerReceipt = PrintToFile(entry, bytes, fullOutputPath, cancellationToken);
        }
        catch (PrintExecutionBlockedException exception)
        {
            if (File.Exists(fullOutputPath))
            {
                throw new PrintSubmissionUncertainException(
                    "MICROSOFT_PDF_OUTPUT_OUTCOME_UNCERTAIN",
                    "The Microsoft PDF driver failed after creating an output file; automatic fallback is disabled to avoid duplicate output.",
                    exception);
            }

            var fallbackReceipt = ExportPdf(
                entry with { OutputMode = PrinterOutputModeRules.PdfExport },
                bytes,
                exportDirectory,
                cancellationToken);

            return new PdfExportExecutionResult(
                fallbackReceipt,
                true,
                $"SMARTPRINT_FALLBACK:{exception.ReasonCode}",
                null);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCompletePdfFile(fullOutputPath))
            {
                try
                {
                    var driverReceipt = ReadExistingPdfExport(fullOutputPath);
                    return new PdfExportExecutionResult(
                        driverReceipt,
                        false,
                        "MICROSOFT_PRINT_TO_PDF",
                        spoolerReceipt.JobId);
                }
                catch (IOException)
                {
                    // The Microsoft print processor may still hold the file open.
                }
                catch (PrintExecutionBlockedException)
                {
                    // A partially-written file is not accepted as a completed export.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        // StartDoc/EndDoc succeeded, but no complete PDF appeared. A job still
        // present in the queue may yet produce output, so never duplicate it.
        WindowsSpoolerJob? remainingJob;
        try
        {
            remainingJob = FindJobByDocumentName(
                entry.QueueName ?? throw new InvalidOperationException("The Microsoft PDF queue name is missing."),
                spoolerReceipt.DocumentName);
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
        {
            throw new PrintSubmissionUncertainException(
                "MICROSOFT_PDF_SPOOLER_STATUS_UNAVAILABLE",
                "The Microsoft PDF spooler accepted the job, but its completion could not be confirmed. Automatic fallback is disabled to avoid duplicate output.",
                exception);
        }

        if (remainingJob is null && !File.Exists(fullOutputPath))
        {
            // The Windows queue has removed the completed job without creating the
            // requested file. This is a confirmed driver-output failure, so fallback
            // is safe: there is no queued job and no PDF at the destination.
            var fallbackReceipt = ExportPdf(
                entry with { OutputMode = PrinterOutputModeRules.PdfExport },
                bytes,
                exportDirectory,
                cancellationToken);
            return new PdfExportExecutionResult(
                fallbackReceipt,
                true,
                "SMARTPRINT_FALLBACK:MICROSOFT_DRIVER_PRODUCED_NO_FILE",
                spoolerReceipt.JobId);
        }

        // The original job is still queued or an incomplete file exists. The owner
        // must reconcile it before any second output is generated.
        throw new PrintSubmissionUncertainException(
            "MICROSOFT_PDF_OUTPUT_TIMEOUT",
            remainingJob is null
                ? $"Windows accepted spooler job {spoolerReceipt.JobId}, but an incomplete PDF exists at the output path."
                : $"Windows accepted spooler job {spoolerReceipt.JobId}, which remains in state {remainingJob.Status}; automatic fallback is disabled.");
    }

    private static bool IsCompletePdfFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 100) return false;

            Span<byte> header = stackalloc byte[5];
            if (stream.Read(header) != header.Length || !header.SequenceEqual("%PDF-"u8))
                return false;

            var tailLength = (int)Math.Min(stream.Length, 8192);
            var tail = new byte[tailLength];
            stream.Position = stream.Length - tailLength;
            stream.ReadExactly(tail);
            return System.Text.Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string GetPdfExportPath(AgentLocalQueueEntry entry, string exportDirectory)
    {
        if (string.IsNullOrWhiteSpace(exportDirectory))
            throw new ArgumentException("A PDF export directory is required.", nameof(exportDirectory));
        var root = Path.GetFullPath(exportDirectory);
        var jobPart = SafeFilePart(entry.JobNumber, 48);
        var documentStem = SafeFilePart(Path.GetFileNameWithoutExtension(entry.DocumentName), 120);
        var brandingSuffix = entry.PdfBrandingEnabled && string.Equals(entry.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ? "_smartprint" : string.Empty;
        return Path.Combine(root, $"{jobPart}_{documentStem}{brandingSuffix}.pdf");
    }

    public PdfExportReceipt ExportPdf(
        AgentLocalQueueEntry entry,
        byte[] bytes,
        string exportDirectory,
        CancellationToken cancellationToken)
    {
        if (bytes.LongLength != entry.SizeBytes ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(entry.Sha256)))
            throw new PrintExecutionBlockedException("LOCAL_DOCUMENT_INTEGRITY_MISMATCH", "The local document does not match the verified upload.");

        using var raster = OpenDocument(bytes, entry.MediaType, entry.ColourMode == "Mono");
        if (raster.PageCount is <= 0 or > 500)
            throw new PrintExecutionBlockedException("INVALID_PAGE_COUNT", "The document contains an unsupported number of pages.");
        var printablePageIndices = GetPrintablePageIndices(raster.PageCount, entry.RejectedPageNumbers);

        Directory.CreateDirectory(exportDirectory);
        var outputPath = GetPdfExportPath(entry, exportDirectory);
        if (File.Exists(outputPath))
            return ReadExistingPdfExport(outputPath);

        var temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var vectorBranding = entry.PdfBrandingEnabled &&
                string.Equals(entry.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) &&
                raster is PdfRasterDocument pdfRaster
                    ? PdfBrandingOverlay.TryCreate(entry, bytes, pdfRaster.HasDigitalSignatures, pdfRaster.HasInteractiveForms, cancellationToken)
                    : null;
            try
            {
                using (var output = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                    FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    if (vectorBranding is not null)
                    {
                        output.Write(vectorBranding.Bytes);
                        output.Flush(true);
                    }
                    else if (!entry.PdfBrandingEnabled &&
                             string.Equals(entry.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) &&
                             entry.Copies == 1 &&
                             (entry.RejectedPageNumbers?.Count ?? 0) == 0)
                    {
                        // With branding off, a normal single-copy full-document PDF
                        // export is the uploaded PDF itself. Do not needlessly rebuild
                        // it as a raster copy and discard its text, forms or PDF objects.
                        output.Write(bytes);
                        output.Flush(true);
                    }
                    else
                    {
                        using var pdf = SKDocument.CreatePdf(output)
                            ?? throw new PrintExecutionBlockedException("PDF_EXPORT_CREATE_FAILED", "The PDF document could not be created.");
                        const int dpi = 300;
                        // A footer is only added by the vector PDFium overlay above.
                        // Never raster-paint a fallback PDF: unsupported/complex PDFs
                        // must not lose selectable text merely to show the brand mark.
                        for (var copy = 0; copy < entry.Copies; copy++)
                        {
                            foreach (var pageIndex in printablePageIndices)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                using var page = raster.RenderPage(pageIndex, dpi);
                                using var bitmap = new SKBitmap(new SKImageInfo(
                                    page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                                var pixels = bitmap.GetPixels();
                                if (pixels == IntPtr.Zero)
                                    throw new PrintExecutionBlockedException("PDF_EXPORT_BITMAP_FAILED", "The page bitmap could not be allocated.");
                                Marshal.Copy(page.Pixels, 0, pixels, page.Pixels.Length);
                                using var image = SKImage.FromBitmap(bitmap)
                                    ?? throw new PrintExecutionBlockedException("PDF_EXPORT_IMAGE_FAILED", "The rendered page could not be encoded.");

                                var landscape = ResolveLandscape(entry.Orientation, page.Width > page.Height);
                                var (paperWidth, paperHeight) = PdfPageSize(entry.PaperSize, landscape);
                                var canvas = pdf.BeginPage(paperWidth, paperHeight);
                                var scale = Math.Min(paperWidth / page.Width, paperHeight / page.Height);
                                var width = page.Width * scale;
                                var height = page.Height * scale;
                                var destination = new SKRect(
                                    (paperWidth - width) / 2f,
                                    (paperHeight - height) / 2f,
                                    (paperWidth + width) / 2f,
                                    (paperHeight + height) / 2f);
                                canvas.Clear(SKColors.White);
                                canvas.DrawImage(image, destination);
                                canvas.Flush();
                                pdf.EndPage();
                            }
                        }
                        pdf.Close();
                    }
                }
            }
            finally
            {
                if (vectorBranding is not null)
                    CryptographicOperations.ZeroMemory(vectorBranding.Bytes);
            }

            var info = new FileInfo(temporaryPath);
            if (!info.Exists || info.Length <= 0)
                throw new PrintExecutionBlockedException("PDF_EXPORT_EMPTY", "The PDF export did not produce a file.");
            byte[] hash;
            using (var read = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                hash = SHA256.HashData(read);
            try
            {
                File.Move(temporaryPath, outputPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(outputPath))
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                return ReadExistingPdfExport(outputPath);
            }
            return new PdfExportReceipt(
                Path.GetFileName(outputPath),
                outputPath,
                info.Length,
                Convert.ToHexString(hash).ToLowerInvariant());
        }
        catch (PrintExecutionBlockedException)
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
        catch (Exception exception)
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(outputPath))
                throw new PrintSubmissionUncertainException("PDF_EXPORT_OUTCOME_UNCERTAIN", "The PDF file may already have been created. Reconcile the output before retrying.", exception);
            throw new PrintExecutionBlockedException("PDF_EXPORT_FAILED", "The PDF could not be saved to the configured shop export folder.", exception);
        }
    }

    public PdfExportReceipt ReadExistingPdfExport(string outputPath)
    {
        if (!File.Exists(outputPath))
            throw new FileNotFoundException("The expected PDF export does not exist.", outputPath);
        using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsCompletePdfFile(outputPath))
            throw new PrintExecutionBlockedException("PDF_EXPORT_FILE_INVALID", "An existing file at the PDF export path is not a complete PDF.");
        stream.Position = 0;
        var size = stream.Length;
        var hash = SHA256.HashData(stream);
        return new PdfExportReceipt(
            Path.GetFileName(outputPath),
            Path.GetFullPath(outputPath),
            size,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    internal static byte[] LoadQuantaEdgeMark()
    {
        using var stream = typeof(WindowsPrintExecutor).Assembly.GetManifestResourceStream(
            "SmartPrint.Agent.Branding.quantaedge-mark.png")
            ?? throw new PrintExecutionBlockedException(
                "PDF_BRANDING_ASSET_MISSING",
                "The official QuantaEdge logo asset is not available in this Agent build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static (float Width, float Height) PdfPageSize(string paperSize, bool landscape)
    {
        var (width, height) = paperSize.Trim().ToUpperInvariant() switch
        {
            "A4" => (595.276f, 841.89f),
            "A3" => (841.89f, 1190.55f),
            "A5" => (419.528f, 595.276f),
            "LETTER" => (612f, 792f),
            "LEGAL" => (612f, 1008f),
            _ => throw new PrintExecutionBlockedException("PAPER_SIZE_UNSUPPORTED", $"Paper '{paperSize}' is not supported for PDF export.")
        };
        return landscape ? (height, width) : (width, height);
    }

    private static bool ResolveLandscape(string orientation, bool automaticLandscape) =>
        orientation.Trim().ToUpperInvariant() switch
        {
            "LANDSCAPE" => true,
            "PORTRAIT" => false,
            _ => automaticLandscape
        };

    private static string SafeFilePart(string value, int maximumLength)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((value ?? string.Empty).Select(character =>
            invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(safe)) safe = "document";
        return safe.Length <= maximumLength ? safe : safe[..maximumLength];
    }

    public WindowsSpoolerJob? FindJobByDocumentName(string queueName, string documentName)
    {
        using var handle = OpenQueue(queueName);
        _ = EnumJobsW(handle.Value, 0, 500, 1, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumJobsW(handle.Value, 0, 500, 1, buffer, needed, out needed, out var returned))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate the Windows print queue.");
            var size = Marshal.SizeOf<JobInfo1W>();
            for (var index = 0; index < returned; index++)
            {
                var item = Marshal.PtrToStructure<JobInfo1W>(IntPtr.Add(buffer, index * size));
                if (string.Equals(Marshal.PtrToStringUni(item.DocumentName), documentName, StringComparison.Ordinal))
                    return ConvertJob(item);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public WindowsSpoolerJob? GetJob(string queueName, int jobId)
    {
        using var handle = OpenQueue(queueName);
        _ = GetJobW(handle.Value, jobId, 1, IntPtr.Zero, 0, out var needed);
        var error = Marshal.GetLastWin32Error();
        if (needed <= 0)
        {
            if (error is 87 or 1168) return null;
            if (error == 0) return null;
            throw new Win32Exception(error, "Could not query the Windows print job.");
        }
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetJobW(handle.Value, jobId, 1, buffer, needed, out needed))
            {
                error = Marshal.GetLastWin32Error();
                if (error is 87 or 1168) return null;
                throw new Win32Exception(error, "Could not read the Windows print job.");
            }
            return ConvertJob(Marshal.PtrToStructure<JobInfo1W>(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IntPtr CreateDevMode(IntPtr printer, string queueName, AgentLocalQueueEntry entry, bool landscape)
    {
        var size = DocumentPropertiesW(IntPtr.Zero, printer, queueName, IntPtr.Zero, IntPtr.Zero, 0);
        if (size < Marshal.SizeOf<DevModeW>())
            throw new PrintExecutionBlockedException("PRINTER_DEVMODE_UNAVAILABLE", "The printer driver did not provide valid settings.");

        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            if (DocumentPropertiesW(IntPtr.Zero, printer, queueName, pointer, IntPtr.Zero, 2) != 1)
                throw new PrintExecutionBlockedException("PRINTER_DEVMODE_READ_FAILED", "The default printer settings could not be read.");

            var mode = Marshal.PtrToStructure<DevModeW>(pointer);
            var paper = PaperCode(entry.PaperSize);
            if (SupportsPaper(queueName, pointer, paper) is false)
                throw new PrintExecutionBlockedException("PAPER_SIZE_UNSUPPORTED", $"The installed driver explicitly reports that {entry.PaperSize} paper is unsupported.");

            var orientationCapability = DeviceCapabilitiesW(queueName, null, DcOrientation, IntPtr.Zero, pointer);
            if (landscape && orientationCapability == 0)
                throw new PrintExecutionBlockedException("LANDSCAPE_UNSUPPORTED", "The installed printer driver reports that landscape orientation is not supported.");

            mode.Fields |= DmOrientation | DmPaperSize;
            mode.Orientation = landscape ? (short)2 : (short)1;
            mode.PaperSize = paper;

            if (entry.ColourMode == "Colour")
            {
                var colourCapability = DeviceCapabilitiesW(queueName, null, DcColorDevice, IntPtr.Zero, pointer);
                if (PrinterCapabilityRules.IsExplicitlyUnsupportedCapability(colourCapability))
                    throw new PrintExecutionBlockedException("COLOUR_UNSUPPORTED", "The selected printer driver explicitly reports that colour is unsupported.");
                // A missing/failed capability report is unknown, not negative.
                // Ask the driver to apply the option and validate its response below.
                mode.Color = 2;
                mode.Fields |= DmColor;
            }
            else if ((mode.Fields & DmColor) != 0) mode.Color = 1;

            if (entry.IsDuplex)
            {
                var duplexCapability = DeviceCapabilitiesW(queueName, null, DcDuplex, IntPtr.Zero, pointer);
                if (PrinterCapabilityRules.IsExplicitlyUnsupportedCapability(duplexCapability))
                    throw new PrintExecutionBlockedException("DUPLEX_UNSUPPORTED", "The selected printer driver explicitly reports that duplex printing is unsupported.");
                mode.Duplex = 2;
                mode.Fields |= DmDuplex;
            }
            else if ((mode.Fields & DmDuplex) != 0) mode.Duplex = 1;

            // Keep driver copies at one because software expands collated copies inside this spooler job.
            if ((mode.Fields & 0x00000100) != 0) mode.Copies = 1;
            Marshal.StructureToPtr(mode, pointer, false);
            if (DocumentPropertiesW(IntPtr.Zero, printer, queueName, pointer, pointer, 10) != 1)
                throw new PrintExecutionBlockedException("PRINTER_SETTINGS_REJECTED", "The printer driver rejected the requested print settings.");

            var validated = Marshal.PtrToStructure<DevModeW>(pointer);
            if ((validated.Fields & DmPaperSize) == 0 || validated.PaperSize != paper)
                throw new PrintExecutionBlockedException("PAPER_SIZE_REJECTED", "The printer driver did not accept the requested paper size.");
            var expectedOrientation = landscape ? (short)2 : (short)1;
            if (!PrinterCapabilityRules.IsRequestedOrientationAccepted(
                    (validated.Fields & DmOrientation) != 0, validated.Orientation, expectedOrientation))
                throw new PrintExecutionBlockedException("ORIENTATION_UNCONFIRMED", "The printer driver could not confirm the requested orientation. No pages were printed.");
            if (entry.IsDuplex && ((validated.Fields & DmDuplex) == 0 || validated.Duplex != 2))
                throw new PrintExecutionBlockedException("DUPLEX_REJECTED", "The driver did not accept duplex mode.");
            if (entry.ColourMode == "Colour" && ((validated.Fields & DmColor) == 0 || validated.Color != 2))
                throw new PrintExecutionBlockedException("COLOUR_REJECTED", "The driver did not accept colour mode.");
            return pointer;
        }
        catch
        {
            Marshal.FreeHGlobal(pointer);
            throw;
        }
    }

    private static bool? SupportsPaper(string queueName, IntPtr devMode, short paperCode)
    {
        var count = DeviceCapabilitiesW(queueName, null, DcPapers, IntPtr.Zero, devMode);
        if (count <= 0 || count > 256) return null;

        var buffer = Marshal.AllocHGlobal(count * sizeof(short));
        try
        {
            var actual = DeviceCapabilitiesW(queueName, null, DcPapers, buffer, devMode);
            if (actual <= 0 || actual > count) return null;

            var reportedPapers = new short[actual];
            for (var i = 0; i < actual; i++)
                reportedPapers[i] = Marshal.ReadInt16(buffer, i * sizeof(short));
            return PrinterCapabilityRules.EvaluatePaperSupport(count, actual, reportedPapers, paperCode);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static short PaperCode(string paper) => paper.Trim().ToUpperInvariant() switch
    {
        "A4" => 9, "A3" => 8, "A5" => 11, "LETTER" => 1, "LEGAL" => 5,
        _ => throw new PrintExecutionBlockedException("PAPER_SIZE_UNSUPPORTED", $"Paper '{paper}' is not supported in this release.")
    };

    private static int[] GetPrintablePageIndices(int pageCount, IReadOnlyList<int>? rejectedPageNumbers)
    {
        var rejectedPages = rejectedPageNumbers ?? Array.Empty<int>();
        if (rejectedPages.Distinct().Count() != rejectedPages.Count ||
            rejectedPages.Any(page => page < 1 || page > pageCount))
            throw new PrintExecutionBlockedException("REJECTED_PAGE_SELECTION_INVALID", "The server supplied an invalid rejected-page selection.");

        var printable = Enumerable.Range(0, pageCount).Where(index => !rejectedPages.Contains(index + 1)).ToArray();
        if (printable.Length == 0)
            throw new PrintExecutionBlockedException("NO_ACCEPTED_PAGES", "Every page was rejected. The file must be fully refunded instead of printed.");
        return printable;
    }

    private static IRasterDocument OpenDocument(byte[] bytes, string mediaType, bool mono)
    {
        if (mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)) return new PdfRasterDocument(bytes, mono);
        if (mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            return new ImageRasterDocument(bytes, mono);
        throw new PrintExecutionBlockedException("DOCUMENT_TYPE_UNSUPPORTED", $"The document type '{mediaType}' cannot be printed by this agent.");
    }

    private static int DrawPageAtPhysicalScale(IntPtr dc, RasterPage page, IdDocumentPagePlacement placement)
    {
        _ = SetStretchBltMode(dc, Halftone);
        _ = SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = page.Width, Height = -page.Height,
                Planes = 1, BitCount = 32, Compression = BiRgb, SizeImage = checked((uint)page.Pixels.Length)
            }
        };
        var pin = GCHandle.Alloc(page.Pixels, GCHandleType.Pinned);
        try
        {
            // The complete A4 raster is mapped to the driver's physical paper
            // bounds. Negative offsets shift the paper origin from the printable
            // origin; scale conversion is DPI-only, never fit-to-printable-area.
            return StretchDIBits(
                dc,
                placement.Left,
                placement.Top,
                placement.Width,
                placement.Height,
                0,
                0,
                page.Width,
                page.Height,
                pin.AddrOfPinnedObject(),
                ref info,
                DibRgbColors,
                SrcCopy);
        }
        finally { pin.Free(); }
    }

    private static int DrawPage(IntPtr dc, RasterPage page, int printableWidth, int printableHeight)
    {
        var scale = Math.Min((double)printableWidth / page.Width, (double)printableHeight / page.Height);
        var width = Math.Max(1, (int)Math.Round(page.Width * scale));
        var height = Math.Max(1, (int)Math.Round(page.Height * scale));
        var left = Math.Max(0, (printableWidth - width) / 2);
        var top = Math.Max(0, (printableHeight - height) / 2);
        _ = SetStretchBltMode(dc, Halftone);
        _ = SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = page.Width, Height = -page.Height,
                Planes = 1, BitCount = 32, Compression = BiRgb, SizeImage = checked((uint)page.Pixels.Length)
            }
        };
        var pin = GCHandle.Alloc(page.Pixels, GCHandleType.Pinned);
        try
        {
            return StretchDIBits(dc, left, top, width, height, 0, 0, page.Width, page.Height,
                pin.AddrOfPinnedObject(), ref info, DibRgbColors, SrcCopy);
        }
        finally { pin.Free(); }
    }

    public static string ResolveSpoolerStatus(uint statusFlags) =>
        // Fault flags take precedence over PRINTING/SPOOLING so an offline
        // or paper-out condition is never hidden behind queue activity.
        (statusFlags & 0x00000020) != 0 ? "OFFLINE"
        : (statusFlags & 0x00000040) != 0 ? "PAPEROUT"
        : (statusFlags & 0x00000001) != 0 ? "PAUSED"
        : (statusFlags & 0x00000002) != 0 ? "ERROR"
        : (statusFlags & 0x00000400) != 0 ? "USERINTERVENTION"
        : (statusFlags & 0x00000010) != 0 ? "PRINTING"
        : (statusFlags & 0x00000008) != 0 ? "SPOOLING"
        : (statusFlags & 0x00000080) != 0 ? "PRINTED"
        : "QUEUED";

    private static WindowsSpoolerJob ConvertJob(JobInfo1W item)
    {
        var status = ResolveSpoolerStatus(item.Status);
        return new WindowsSpoolerJob(checked((int)item.JobId), Marshal.PtrToStringUni(item.DocumentName) ?? string.Empty,
            status, item.Status, checked((int)item.PagesPrinted), checked((int)item.TotalPages));
    }

    private static SafePrinterHandle OpenQueue(string queueName)
    {
        if (!OpenPrinterW(queueName, out var handle, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open printer '{queueName}'.");
        return new SafePrinterHandle(handle);
    }

    private static PrintExecutionBlockedException Blocked(string code, string message) =>
        new(code, message, new Win32Exception(Marshal.GetLastWin32Error()));
    private static PrintSubmissionUncertainException Uncertain(string code, string message) =>
        new(code, message, new Win32Exception(Marshal.GetLastWin32Error()));

    private interface IRasterDocument : IDisposable
    {
        int PageCount { get; }
        bool FirstPageLandscape { get; }
        RasterPage RenderPage(int pageIndex, int dpi);
    }

    private sealed class PdfRasterDocument : IRasterDocument
    {
        private readonly IntPtr documentMemory;
        private readonly int documentMemoryLength;
        private readonly FpdfDocumentT document;
        private readonly bool mono;
        public PdfRasterDocument(byte[] bytes, bool mono)
        {
            _ = PdfiumInitialized.Value;
            documentMemoryLength = bytes.Length;
            documentMemory = Marshal.AllocHGlobal(documentMemoryLength);
            Marshal.Copy(bytes, 0, documentMemory, documentMemoryLength);
            this.mono = mono;
            document = fpdfview.FPDF_LoadMemDocument64(documentMemory, (ulong)bytes.Length, string.Empty);
            if (document is null)
            {
                Marshal.FreeHGlobal(documentMemory);
                throw new PrintExecutionBlockedException("PDF_RENDER_OPEN_FAILED", $"PDFium could not open the PDF (error {fpdfview.FPDF_GetLastError()}).");
            }
            HasDigitalSignatures = fpdf_signature.FPDF_GetSignatureCount(document) != 0;
            HasInteractiveForms = fpdf_formfill.FPDF_GetFormType(document) != 0;
            PageCount = fpdfview.FPDF_GetPageCount(document);
            if (PageCount is <= 0 or > 500)
            {
                fpdfview.FPDF_CloseDocument(document);
                Marshal.FreeHGlobal(documentMemory);
                throw new PrintExecutionBlockedException("PDF_PAGE_COUNT_INVALID", "The PDF page count is not supported.");
            }
            using var page = LoadPage(0);
            FirstPageLandscape = fpdfview.FPDF_GetPageWidthF(page.Value) > fpdfview.FPDF_GetPageHeightF(page.Value);
        }
        public int PageCount { get; }
        public bool FirstPageLandscape { get; }
        public bool HasDigitalSignatures { get; }
        public bool HasInteractiveForms { get; }
        public RasterPage RenderPage(int pageIndex, int dpi)
        {
            using var page = LoadPage(pageIndex);
            var widthPoints = fpdfview.FPDF_GetPageWidthF(page.Value);
            var heightPoints = fpdfview.FPDF_GetPageHeightF(page.Value);
            if (!float.IsFinite(widthPoints) || !float.IsFinite(heightPoints) || widthPoints <= 0 || heightPoints <= 0)
                throw new PrintExecutionBlockedException("PDF_PAGE_DIMENSIONS_INVALID", "A PDF page has invalid dimensions.");
            var scale = Math.Min(dpi / 72d, Math.Sqrt(MaxRasterPixels / (Math.Max(1, widthPoints * dpi / 72d) * Math.Max(1, heightPoints * dpi / 72d))));
            var width = Math.Clamp((int)Math.Ceiling(widthPoints * scale), 1, MaxRasterDimension);
            var height = Math.Clamp((int)Math.Ceiling(heightPoints * scale), 1, MaxRasterDimension);
            var bitmap = fpdfview.FPDFBitmapCreate(width, height, 0);
            if (bitmap is null) throw new PrintExecutionBlockedException("PDF_PAGE_RENDER_FAILED", "PDFium could not allocate a page bitmap.");
            try
            {
                _ = fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                fpdfview.FPDF_RenderPageBitmap(bitmap, page.Value, 0, 0, width, height, 0, 0);
                var stride = fpdfview.FPDFBitmapGetStride(bitmap);
                var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
                if (buffer == IntPtr.Zero || stride < width * 4)
                    throw new PrintExecutionBlockedException("PDF_PAGE_BUFFER_INVALID", "PDFium returned an invalid bitmap.");
                var pixels = new byte[checked(stride * height)];
                Marshal.Copy(buffer, pixels, 0, pixels.Length);
                var packed = CompactRows(pixels, width, height, stride);
                if (mono) ConvertToGrayscale(packed);
                return new RasterPage(width, height, packed, widthPoints, heightPoints);
            }
            finally { fpdfview.FPDFBitmapDestroy(bitmap); }
        }
        public void Dispose()
        {
            fpdfview.FPDF_CloseDocument(document);
            Marshal.Copy(new byte[documentMemoryLength], 0, documentMemory, documentMemoryLength);
            Marshal.FreeHGlobal(documentMemory);
        }
        private LoadedPage LoadPage(int index)
        {
            var page = fpdfview.FPDF_LoadPage(document, index);
            if (page is null) throw new PrintExecutionBlockedException("PDF_PAGE_LOAD_FAILED", $"Could not load PDF page {index + 1}.");
            return new LoadedPage(page);
        }
    }

    private sealed class LoadedPage(FpdfPageT value) : IDisposable
    {
        public FpdfPageT Value { get; } = value;
        public void Dispose() => fpdfview.FPDF_ClosePage(Value);
    }

    private sealed class ImageRasterDocument : IRasterDocument
    {
        private readonly SKImage image;
        private readonly bool mono;
        public ImageRasterDocument(byte[] bytes, bool mono)
        {
            this.mono = mono;
            using var stream = new MemoryStream(bytes, false);
            using var bitmap = SKBitmap.Decode(stream);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0 ||
                (long)bitmap.Width * bitmap.Height > MaxRasterPixels ||
                bitmap.Width > MaxRasterDimension || bitmap.Height > MaxRasterDimension)
                throw new PrintExecutionBlockedException("IMAGE_RENDER_OPEN_FAILED", "The image is invalid or too large to print.");
            image = SKImage.FromBitmap(bitmap) ?? throw new PrintExecutionBlockedException("IMAGE_RENDER_OPEN_FAILED", "Skia could not prepare the image.");
            FirstPageLandscape = image.Width > image.Height;
        }
        public int PageCount => 1;
        public bool FirstPageLandscape { get; }
        public RasterPage RenderPage(int pageIndex, int dpi)
        {
            if (pageIndex != 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, new SKRect(0, 0, image.Width, image.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.Flush();
            var pixels = new byte[checked(bitmap.RowBytes * bitmap.Height)];
            Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
            var compact = CompactRows(pixels, bitmap.Width, bitmap.Height, bitmap.RowBytes);
            if (mono) ConvertToGrayscale(compact);
            return new RasterPage(bitmap.Width, bitmap.Height, compact);
        }
        public void Dispose() => image.Dispose();
    }

    private sealed record RasterPage(int Width, int Height, byte[] Pixels, float WidthPoints = 0, float HeightPoints = 0) : IDisposable
    {
        public void Dispose() => CryptographicOperations.ZeroMemory(Pixels);
    }

    private static byte[] CompactRows(byte[] source, int width, int height, int stride)
    {
        var rowSize = checked(width * 4);
        var result = new byte[checked(rowSize * height)];
        for (var y = 0; y < height; y++) Buffer.BlockCopy(source, y * stride, result, y * rowSize, rowSize);
        CryptographicOperations.ZeroMemory(source);
        return result;
    }

    private static void ConvertToGrayscale(byte[] bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var grey = (byte)((bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8);
            bgra[i] = grey; bgra[i + 1] = grey; bgra[i + 2] = grey; bgra[i + 3] = 255;
        }
    }

    private sealed class SafePrinterHandle(IntPtr value) : IDisposable
    {
        public IntPtr Value { get; } = value;
        public void Dispose() { if (Value != IntPtr.Zero) _ = ClosePrinter(Value); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevModeW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public short Orientation, PaperSize, PaperLength, PaperWidth, Scale, Copies, DefaultSource,
            PrintQuality, Color, Duplex, YResolution, TrueTypeOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency,
            IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfoW
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DataType;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobInfo1W
    {
        public uint JobId;
        public IntPtr PrinterName, MachineName, UserName, DocumentName, DataType, StatusText;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public SystemTime Submitted;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("winspool.drv", EntryPoint="OpenPrinterW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool OpenPrinterW(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", EntryPoint="ClosePrinter", SetLastError=true)]
    private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", EntryPoint="DocumentPropertiesW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern int DocumentPropertiesW(IntPtr hwnd, IntPtr printer, string device, IntPtr output, IntPtr input, int mode);
    [DllImport("winspool.drv", EntryPoint="DeviceCapabilitiesW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern int DeviceCapabilitiesW(string device, string? port, int capability, IntPtr output, IntPtr devMode);
    [DllImport("gdi32.dll", EntryPoint="CreateDCW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr CreateDCW(string driver, string device, string? output, IntPtr devMode);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr oldPoint);
    [DllImport("gdi32.dll", EntryPoint="StartDocW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern int StartDocW(IntPtr dc, ref DocInfoW info);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint="StretchDIBits", SetLastError=true)]
    private static extern int StretchDIBits(IntPtr dc, int xDest, int yDest, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight, IntPtr bits, ref BitmapInfo info, uint usage, uint rop);
    [DllImport("winspool.drv", EntryPoint="EnumJobsW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool EnumJobsW(IntPtr printer, uint first, uint count, uint level, IntPtr jobs, int size, out int needed, out int returned);
    [DllImport("winspool.drv", EntryPoint="GetJobW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool GetJobW(IntPtr printer, int jobId, uint level, IntPtr job, int size, out int needed);
}
