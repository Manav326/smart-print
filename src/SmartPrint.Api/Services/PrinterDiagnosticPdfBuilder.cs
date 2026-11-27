using System.Globalization;
using System.Text;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

/// <summary>
/// Builds a compact PDF around portal-rendered JPEG diagnostic sheets. It does
/// not add a PDF dependency or a second renderer; the Agent still uses the
/// standard PDFium/Windows spooler execution path.
/// </summary>
public static class PrinterDiagnosticPdfBuilder
{
    private const int MaximumPages = 2;
    private const int MaximumPageBytes = 4 * 1024 * 1024;

    public static byte[] CreatePdf(IReadOnlyList<byte[]> jpegPages, string paperSize)
    {
        ArgumentNullException.ThrowIfNull(jpegPages);
        if (jpegPages.Count is < 1 or > MaximumPages)
            throw new ArgumentOutOfRangeException(nameof(jpegPages), "A diagnostic contains one page, or two pages when duplex is supported.");

        var (width, height) = ResolvePageSize(paperSize);
        foreach (var page in jpegPages)
        {
            if (page is null || page.Length is < 100 or > MaximumPageBytes ||
                page.Length < 3 || page[0] != 0xff || page[1] != 0xd8 || page[2] != 0xff ||
                page[^2] != 0xff || page[^1] != 0xd9)
                throw new ArgumentException("Each diagnostic page must be a complete JPEG image within the size limit.", nameof(jpegPages));
        }

        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
        var objectCount = 2 + jpegPages.Count * 3;
        var offsets = new long[objectCount + 1];

        WriteObject(output, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, jpegPages.Count).Select(index => $"{3 + index * 3} 0 R"));
        WriteObject(output, offsets, 2, $"<< /Type /Pages /Kids [{kids}] /Count {jpegPages.Count} >>");

        for (var index = 0; index < jpegPages.Count; index++)
        {
            var pageId = 3 + index * 3;
            var contentId = pageId + 1;
            var imageId = pageId + 2;
            var content = Encoding.ASCII.GetBytes(
                string.Create(CultureInfo.InvariantCulture,
                    $"q\n{width:0.###} 0 0 {height:0.###} 0 0 cm\n/Im0 Do\nQ\n"));

            WriteObject(output, offsets, pageId,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width.ToString("0.###", CultureInfo.InvariantCulture)} {height.ToString("0.###", CultureInfo.InvariantCulture)}] /Resources << /XObject << /Im0 {imageId} 0 R >> >> /Contents {contentId} 0 R >>");

            offsets[contentId] = output.Position;
            WriteAscii(output, $"{contentId} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            output.Write(content);
            WriteAscii(output, "endstream\nendobj\n");

            offsets[imageId] = output.Position;
            WriteAscii(output,
                $"{imageId} 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegPages[index].Length} >>\nstream\n");
            output.Write(jpegPages[index]);
            WriteAscii(output, "\nendstream\nendobj\n");
        }

        var xrefOffset = output.Position;
        WriteAscii(output, $"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (var id = 1; id <= objectCount; id++)
            WriteAscii(output, $"{offsets[id]:D10} 00000 n \n");
        WriteAscii(output,
            $"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static (double Width, double Height) ResolvePageSize(string paperSize) =>
        paperSize.Trim().ToUpperInvariant() switch
        {
            "A4" => (595.276, 841.89),
            "A3" => (841.89, 1190.55),
            "A5" => (419.528, 595.276),
            "LETTER" => (612, 792),
            "LEGAL" => (612, 1008),
            _ => throw new ArgumentException("The diagnostic paper size is not supported.", nameof(paperSize))
        };

    private static void WriteObject(Stream output, long[] offsets, int id, string body)
    {
        offsets[id] = output.Position;
        WriteAscii(output, $"{id} 0 obj\n{body}\nendobj\n");
    }

    private static void WriteAscii(Stream output, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        output.Write(bytes);
    }
}
