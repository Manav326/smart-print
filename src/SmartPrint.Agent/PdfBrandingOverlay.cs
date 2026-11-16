using System.Net.Http;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PDFiumCore;
using SkiaSharp;

namespace SmartPrint.Agent;

internal sealed record PdfBrandingOverlayResult(byte[] Bytes, bool FooterApplied);

internal static class PdfBrandingOverlay
{
    private const int AnnotationRenderFlag = 1;
    private const int MaximumRasterDimension = 10_000;
    private const long MaximumRasterPixels = 24_000_000;
    private const float PageSizeTolerancePoints = 4f;

    internal static bool CanModifyDocument(bool hasDigitalSignatures, bool hasInteractiveForms) =>
        !hasDigitalSignatures && !hasInteractiveForms;

    internal static PdfBrandingOverlayResult? TryCreate(
        AgentLocalQueueEntry entry,
        byte[] originalBytes,
        bool hasDigitalSignatures,
        bool hasInteractiveForms,
        CancellationToken cancellationToken)
    {
        if (!entry.PdfBrandingEnabled ||
            !string.Equals(entry.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            entry.Copies != 1 ||
            (entry.RejectedPageNumbers?.Count ?? 0) != 0)
            return null;

        // A signed or interactive document is returned byte-for-byte unchanged.
        // We never invalidate a signature or flatten a form just to add a footer.
        if (!CanModifyDocument(hasDigitalSignatures, hasInteractiveForms))
            return new PdfBrandingOverlayResult(originalBytes.ToArray(), false);

        IntPtr documentMemory = IntPtr.Zero;
        FpdfDocumentT? document = null;
        try
        {
            documentMemory = Marshal.AllocHGlobal(originalBytes.Length);
            Marshal.Copy(originalBytes, 0, documentMemory, originalBytes.Length);
            document = fpdfview.FPDF_LoadMemDocument64(documentMemory, (ulong)originalBytes.Length, string.Empty);
            if (document is null)
                return null;

            var pageCount = fpdfview.FPDF_GetPageCount(document);
            if (pageCount is <= 0 or > 500)
                return null;

            var footerApplied = false;
            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = fpdfview.FPDF_LoadPage(document, pageIndex);
                if (page is null)
                    return null;
                try
                {
                    // Rotated page coordinate systems are intentionally left alone.
                    // The original page remains present and is still rendered normally.
                    if (fpdf_edit.FPDFPageGetRotation(page) != 0)
                        continue;

                    var pageWidth = fpdfview.FPDF_GetPageWidthF(page);
                    var pageHeight = fpdfview.FPDF_GetPageHeightF(page);
                    if (!float.IsFinite(pageWidth) || !float.IsFinite(pageHeight) ||
                        pageWidth <= 0 || pageHeight <= 0 ||
                        !MatchesRequestedPaperSize(entry, pageWidth, pageHeight))
                    {
                        // Do not silently ignore the owner's paper-size choice. Let the
                        // existing print-copy exporter handle documents needing reformatting.
                        return null;
                    }

                    if (!HasSafeFooterBand(page, pageWidth, pageHeight))
                        continue;

                    if (!AddFooter(document, page, pageWidth, pageHeight, entry.ColourMode == "Mono"))
                        return null;
                    if (fpdf_edit.FPDFPageGenerateContent(page) != 1)
                        return null;
                    footerApplied = true;
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }

            // No eligible blank footer band: keep the exact original PDF bytes.
            if (!footerApplied)
                return new PdfBrandingOverlayResult(originalBytes.ToArray(), false);

            using var output = new MemoryStream();
            using var fileWriter = new FPDF_FILEWRITE_();
            PDFiumCore.Delegates.Func_int___IntPtr___IntPtr_ulong writeBlock = (_, data, size) =>
            {
                try
                {
                    if (size > int.MaxValue)
                        return 0;
                    var chunk = new byte[checked((int)size)];
                    if (chunk.Length > 0)
                        Marshal.Copy(data, chunk, 0, chunk.Length);
                    output.Write(chunk, 0, chunk.Length);
                    return 1;
                }
                catch
                {
                    return 0;
                }
            };
            fileWriter.Version = 1;
            fileWriter.WriteBlock = writeBlock;
            if (fpdf_save.FPDF_SaveAsCopy(document, fileWriter, 0) != 1)
                return null;
            GC.KeepAlive(writeBlock);

            var result = output.ToArray();
            if (result.Length < 100 || !result.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            {
                CryptographicOperations.ZeroMemory(result);
                return null;
            }
            return new PdfBrandingOverlayResult(result, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
        finally
        {
            if (document is not null)
                fpdfview.FPDF_CloseDocument(document);
            if (documentMemory != IntPtr.Zero)
            {
                Marshal.Copy(new byte[originalBytes.Length], 0, documentMemory, originalBytes.Length);
                Marshal.FreeHGlobal(documentMemory);
            }
        }
    }

    private static bool MatchesRequestedPaperSize(AgentLocalQueueEntry entry, float width, float height)
    {
        var landscape = entry.Orientation.Trim().ToUpperInvariant() switch
        {
            "LANDSCAPE" => true,
            "PORTRAIT" => false,
            _ => width > height
        };
        var expected = entry.PaperSize.Trim().ToUpperInvariant() switch
        {
            "A4" => (595.276f, 841.89f),
            "A3" => (841.89f, 1190.55f),
            "A5" => (419.528f, 595.276f),
            "LETTER" => (612f, 792f),
            "LEGAL" => (612f, 1008f),
            _ => (0f, 0f)
        };
        if (expected.Item1 <= 0 || expected.Item2 <= 0)
            return false;
        var expectedWidth = landscape ? expected.Item2 : expected.Item1;
        var expectedHeight = landscape ? expected.Item1 : expected.Item2;
        return Math.Abs(width - expectedWidth) <= PageSizeTolerancePoints &&
            Math.Abs(height - expectedHeight) <= PageSizeTolerancePoints;
    }

    private static bool HasSafeFooterBand(FpdfPageT page, float widthPoints, float heightPoints)
    {
        var scale = Math.Min(150d / 72d,
            Math.Sqrt(MaximumRasterPixels / (Math.Max(1d, widthPoints) * Math.Max(1d, heightPoints))));
        var width = Math.Clamp((int)Math.Ceiling(widthPoints * scale), 1, MaximumRasterDimension);
        var height = Math.Clamp((int)Math.Ceiling(heightPoints * scale), 1, MaximumRasterDimension);
        if ((long)width * height > MaximumRasterPixels)
            return false;

        var bitmap = fpdfview.FPDFBitmapCreate(width, height, 0);
        if (bitmap is null)
            return false;
        try
        {
            _ = fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, AnnotationRenderFlag);
            var stride = fpdfview.FPDFBitmapGetStride(bitmap);
            var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
            if (buffer == IntPtr.Zero || stride < width * 4)
                return false;
            var pixels = new byte[checked(stride * height)];
            try
            {
                Marshal.Copy(buffer, pixels, 0, pixels.Length);
                var packed = new byte[checked(width * height * 4)];
                var rowBytes = checked(width * 4);
                for (var row = 0; row < height; row++)
                    Buffer.BlockCopy(pixels, row * stride, packed, row * rowBytes, rowBytes);
                var bandHeight = checked((int)Math.Ceiling(PdfBrandingRules.RequiredBlankBandPoints * width / widthPoints));
                return PdfBrandingRules.HasSafeFooterBand(packed, width, height, bandHeight);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pixels);
            }
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    private static bool AddFooter(
        FpdfDocumentT document,
        FpdfPageT page,
        float pageWidth,
        float pageHeight,
        bool monochrome)
    {
        const float margin = 18f;
        const float logoSize = 8f;
        const float gap = 2.5f;
        const float wordSize = 7f;
        const float bySize = 5.6f;
        const float textGap = 2.2f;
        const float baseline = 4.1f;

        using var regularTypeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
        using var boldTypeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        using var smartFont = new SKFont(boldTypeface, wordSize, 1f, 0f);
        using var byFont = new SKFont(regularTypeface, bySize, 1f, 0f);
        using var wordFont = new SKFont(boldTypeface, wordSize, 1f, 0f);
        var smartWidth = smartFont.MeasureText("SmartPrint");
        var byWidth = byFont.MeasureText("by");
        var quantaWidth = wordFont.MeasureText("Quanta");
        var edgeWidth = wordFont.MeasureText("Edge");
        var totalWidth = logoSize + gap + smartWidth + textGap + byWidth + textGap + quantaWidth + edgeWidth;
        if (totalWidth > pageWidth - margin * 2 || pageHeight < 24)
            return false;

        var divider = fpdf_edit.FPDFPageObjCreateNewRect(margin, 15f, pageWidth - margin * 2, .35f);
        if (divider is null)
            return false;
        if (fpdf_edit.FPDFPageObjSetFillColor(divider,
                monochrome ? 170u : 229u,
                monochrome ? 176u : 234u,
                monochrome ? 185u : 243u, 255) != 1 ||
            fpdf_edit.FPDFPathSetDrawMode(divider, 2, 0) != 1 ||
            fpdf_edit.FPDFPageInsertObject(page, divider) != 1)
        {
            fpdf_edit.FPDFPageObjDestroy(divider);
            return false;
        }

        var left = (pageWidth - totalWidth) / 2f;
        var logoLeft = left;
        var logoBottom = 5.5f;
        if (!AddLogo(document, page, logoLeft, logoBottom, logoSize, monochrome))
            return false;

        var textX = left + logoSize + gap;
        if (!AddText(document, page, "SmartPrint", "Helvetica-Bold", wordSize,
                monochrome ? (17u, 24u, 39u) : (21u, 32u, 56u), textX, baseline))
            return false;
        textX += smartWidth + textGap;
        if (!AddText(document, page, "by", "Helvetica", bySize,
                monochrome ? (75u, 85u, 99u) : (102u, 112u, 133u), textX, baseline))
            return false;
        textX += byWidth + textGap;
        if (!AddText(document, page, "Quanta", "Helvetica-Bold", wordSize,
                monochrome ? (17u, 24u, 39u) : (21u, 32u, 56u), textX, baseline))
            return false;
        textX += quantaWidth;
        if (!AddText(document, page, "Edge", "Helvetica-Bold", wordSize,
                monochrome ? (17u, 24u, 39u) : (249u, 115u, 22u), textX, baseline))
            return false;
        return true;
    }

    private static bool AddText(
        FpdfDocumentT document,
        FpdfPageT page,
        string value,
        string font,
        float fontSize,
        (uint R, uint G, uint B) colour,
        float x,
        float y)
    {
        var textObject = fpdf_edit.FPDFPageObjNewTextObj(document, font, fontSize);
        if (textObject is null)
            return false;
        var utf16 = Encoding.Unicode.GetBytes(value + "\0");
        var codeUnits = new ushort[utf16.Length / sizeof(ushort)];
        Buffer.BlockCopy(utf16, 0, codeUnits, 0, utf16.Length);
        if (fpdf_edit.FPDFTextSetText(textObject, ref codeUnits[0]) != 1 ||
            fpdf_edit.FPDFPageObjSetFillColor(textObject, colour.R, colour.G, colour.B, 255) != 1)
        {
            fpdf_edit.FPDFPageObjDestroy(textObject);
            return false;
        }
        fpdf_edit.FPDFPageObjTransform(textObject, 1, 0, 0, 1, x, y);
        if (fpdf_edit.FPDFPageInsertObject(page, textObject) != 1)
        {
            fpdf_edit.FPDFPageObjDestroy(textObject);
            return false;
        }
        return true;
    }

    private static bool AddLogo(
        FpdfDocumentT document,
        FpdfPageT page,
        float x,
        float y,
        float size,
        bool monochrome)
    {
        using var original = SKBitmap.Decode(WindowsPrintExecutor.LoadQuantaEdgeMark());
        if (original is null || original.Width <= 0 || original.Height <= 0)
            return false;
        using var logo = new SKBitmap(new SKImageInfo(original.Width, original.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(logo))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(original, 0, 0);
            canvas.Flush();
        }
        if (monochrome)
        {
            for (var row = 0; row < logo.Height; row++)
            for (var column = 0; column < logo.Width; column++)
            {
                var pixel = logo.GetPixel(column, row);
                if (pixel.Alpha > 0)
                    logo.SetPixel(column, row, new SKColor(17, 24, 39, pixel.Alpha));
            }
        }

        var bitmap = fpdfview.FPDFBitmapCreate(logo.Width, logo.Height, 1);
        if (bitmap is null)
            return false;
        try
        {
            var stride = fpdfview.FPDFBitmapGetStride(bitmap);
            var destination = fpdfview.FPDFBitmapGetBuffer(bitmap);
            if (destination == IntPtr.Zero || stride < logo.Width * 4)
                return false;
            var pixels = new byte[checked(stride * logo.Height)];
            try
            {
                for (var row = 0; row < logo.Height; row++)
                    Marshal.Copy(IntPtr.Add(logo.GetPixels(), row * logo.RowBytes), pixels, row * stride, logo.Width * 4);
                Marshal.Copy(pixels, 0, destination, pixels.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pixels);
            }

            var imageObject = fpdf_edit.FPDFPageObjNewImageObj(document);
            if (imageObject is null)
                return false;
            if (fpdf_edit.FPDFImageObjSetBitmap(page, 1, imageObject, bitmap) != 1 ||
                fpdf_edit.FPDFImageObjSetMatrix(imageObject, size, 0, 0, size, x, y) != 1 ||
                fpdf_edit.FPDFPageInsertObject(page, imageObject) != 1)
            {
                fpdf_edit.FPDFPageObjDestroy(imageObject);
                return false;
            }
            return true;
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }
}
