using System.Globalization;
using System.Text;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public sealed record PrinterDiagnosticArtworkMetadata(
    string PrinterName,
    string QueueName,
    string ConnectionType,
    string OutputType,
    string CapabilityEvidence);

/// <summary>
/// Builds a server-authored, vector-only diagnostic PDF. No client document,
/// image, markup or arbitrary printable bytes are accepted by this builder.
/// The Windows Agent still uses the normal PDFium/spooler execution path.
/// </summary>
public static class PrinterDiagnosticPdfBuilder
{
    private const double DesignWidth = 595.276;
    private const double DesignHeight = 841.89;
    private const int MaximumPageCount = 2;

    public static byte[] CreatePdf(
        PrinterDiagnosticSettings settings,
        PrinterDiagnosticArtworkMetadata metadata,
        DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(metadata);

        var (pageWidth, pageHeight) = ResolvePageSize(settings.PaperSize);
        var expectedPageCount = settings.IsDuplex ? 2 : 1;
        if (settings.PageCount != expectedPageCount || settings.PageCount is < 1 or > MaximumPageCount)
            throw new ArgumentException("The diagnostic capability profile has an invalid page count.", nameof(settings));
        if (!string.Equals(settings.ColourMode, "Mono", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(settings.ColourMode, "Colour", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The diagnostic colour mode is invalid.", nameof(settings));

        var scale = Math.Min(pageWidth / DesignWidth, pageHeight / DesignHeight);
        var offsetX = (pageWidth - DesignWidth * scale) / 2d;
        var offsetY = (pageHeight - DesignHeight * scale) / 2d;
        var pages = new List<byte[]>(settings.PageCount)
        {
            BuildPage(
                DrawFirstPage(settings, metadata, createdUtc),
                scale, offsetX, offsetY)
        };
        if (settings.IsDuplex)
        {
            pages.Add(BuildPage(
                DrawSecondPage(settings, metadata),
                scale, offsetX, offsetY));
        }

        return AssemblePdf(pages, pageWidth, pageHeight);
    }

    private static string DrawFirstPage(
        PrinterDiagnosticSettings settings,
        PrinterDiagnosticArtworkMetadata metadata,
        DateTimeOffset createdUtc)
    {
        var sb = new StringBuilder(12_000);
        DrawPageBackground(sb);
        DrawBrandMark(sb);
        DrawText(sb, "SmartPrint", 111, 77, 27, bold: true, Ink);
        DrawText(sb, "PRINTER DIAGNOSTIC", 543, 76, 14, bold: true, Violet, alignRight: true);
        DrawLine(sb, 52, 105, 543, 105, Border, 1.1);

        DrawText(sb, "One controlled test copy - not a customer order", 52, 132, 11, bold: false, Muted);
        DrawText(sb, "TEMPLATE SP-DIAG-1", 543, 132, 8, bold: true, Muted, alignRight: true);

        DrawPanel(sb, 52, 151, 491, 105);
        DrawText(sb, "PRINTER", 66, 174, 7, bold: true, Muted);
        DrawText(sb, SafeLabel(metadata.PrinterName, 46), 66, 194, 12, bold: true, Ink);
        DrawText(sb, "WINDOWS QUEUE", 66, 216, 7, bold: true, Muted);
        DrawText(sb, SafeLabel(metadata.QueueName, 54), 66, 234, 9, bold: false, Ink);
        DrawText(sb, "CONNECTION", 371, 174, 7, bold: true, Muted);
        DrawText(sb, SafeLabel(metadata.ConnectionType, 20), 371, 194, 10, bold: true, Ink);
        DrawText(sb, "OUTPUT PROFILE", 371, 216, 7, bold: true, Muted);
        DrawText(sb, SafeLabel(metadata.OutputType, 22), 371, 234, 9, bold: true, Ink);

        DrawText(sb, "DOCUMENT / FONT / GLYPH RENDERING", 52, 289, 9, bold: true, Ink);
        DrawText(sb, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", 60, 316, 14, bold: true, Ink);
        DrawText(sb, "abcdefghijklmnopqrstuvwxyz  0123456789", 60, 340, 12, bold: false, Ink);
        DrawText(sb, "Aa  Bb  Cc  Oo  00  Il  11  @  #  %  &  *  +  =  /", 60, 364, 11, bold: false, Ink);
        DrawText(sb, "PUNCTUATION: . , : ; ! ? ( ) [ ] { } - _ apostrophe", 60, 388, 10, bold: false, Ink);

        DrawText(sb, "PAGE GEOMETRY / ALIGNMENT / MARGINS", 52, 430, 9, bold: true, Ink);
        DrawGeometryTest(sb);

        DrawText(sb, "MONOCHROME / GRAYSCALE", 52, 685, 9, bold: true, Ink);
        DrawGrayRamp(sb, 698);

        if (string.Equals(settings.ColourMode, "Colour", StringComparison.OrdinalIgnoreCase))
        {
            DrawText(sb, "COLOUR CHECK (REPORTED BY INSTALLED DRIVER)", 52, 754, 8, bold: true, Ink);
            DrawColorRamp(sb, 766);
        }
        else
        {
            DrawText(sb, "Colour was not explicitly confirmed by the installed driver; this test is monochrome.", 52, 764, 8, bold: false, Muted);
        }

        DrawLine(sb, 52, 805, 543, 805, Border, 0.8);
        DrawText(sb, "ONE COPY", 52, 824, 7, bold: true, Violet);
        DrawText(sb, $"{settings.PaperSize}  |  {settings.ColourMode}  |  {(settings.IsDuplex ? "DUPLEX" : "SIMPLEX")}", 543, 824, 7, bold: true, Muted, alignRight: true);
        DrawText(sb, SafeLabel(createdUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture), 28), 52, 838, 6, bold: false, Muted);
        return sb.ToString();
    }

    private static string DrawSecondPage(
        PrinterDiagnosticSettings settings,
        PrinterDiagnosticArtworkMetadata metadata)
    {
        var sb = new StringBuilder(8_000);
        DrawPageBackground(sb);
        DrawBrandMark(sb);
        DrawText(sb, "SmartPrint", 111, 77, 27, bold: true, Ink);
        DrawText(sb, "DUPLEX REVERSE SIDE", 543, 76, 14, bold: true, Violet, alignRight: true);
        DrawLine(sb, 52, 105, 543, 105, Border, 1.1);

        DrawText(sb, "This is the reverse side of the same one-copy diagnostic sheet.", 52, 134, 10, bold: false, Muted);
        DrawPanel(sb, 52, 166, 491, 430);
        DrawText(sb, "FRONT-TO-BACK REGISTRATION", 297.5, 195, 10, bold: true, Ink, alignCenter: true);
        DrawRegistrationTarget(sb, 104, 260);
        DrawRegistrationTarget(sb, 491, 260);
        DrawRegistrationTarget(sb, 104, 502);
        DrawRegistrationTarget(sb, 491, 502);
        DrawText(sb, "TOP EDGE", 297.5, 246, 8, bold: true, Muted, alignCenter: true);
        DrawText(sb, "Check orientation and alignment against the first page.", 297.5, 380, 9, bold: false, Muted, alignCenter: true);
        DrawText(sb, SafeLabel(metadata.PrinterName, 46), 297.5, 409, 10, bold: true, Ink, alignCenter: true);
        DrawText(sb, SafeLabel(metadata.QueueName, 54), 297.5, 430, 8, bold: false, Muted, alignCenter: true);

        DrawText(sb, "REPEATED EDGE / LINE CHECK", 52, 640, 9, bold: true, Ink);
        for (var index = 0; index < 6; index++)
        {
            var y = 666 + index * 20;
            var shade = 0.12 + (index * 0.13);
            DrawLine(sb, 60, y, 535, y, Gray(shade), index % 2 == 0 ? 1.3 : 0.7);
        }

        DrawLine(sb, 52, 805, 543, 805, Border, 0.8);
        DrawText(sb, "DUPLEX TEST - PAGE 2 OF 2", 52, 824, 7, bold: true, Violet);
        DrawText(sb, settings.PaperSize, 543, 824, 7, bold: true, Muted, alignRight: true);
        DrawText(sb, "Owner must verify the physical output; spooler acceptance is not proof of paper output.", 52, 838, 6, bold: false, Muted);
        return sb.ToString();
    }

    private static void DrawPageBackground(StringBuilder sb) =>
        sb.Append("q\n1 1 1 rg\n0 0 ").Append(F(DesignWidth)).Append(' ')
            .Append(F(DesignHeight)).Append(" re f\nQ\n");

    private static void DrawBrandMark(StringBuilder sb)
    {
        // Vector redraw of the repository's official SmartPrint printer mark.
        // It is kept monochrome-friendly and contains no externally supplied data.
        DrawRect(sb, 54, 51, 47, 40, Violet, fill: true);
        DrawRect(sb, 64, 43, 27, 40, White, fill: true);
        DrawRect(sb, 64, 43, 27, 40, Violet, fill: false, lineWidth: 1.5);
        DrawRect(sb, 65, 64, 42, 21, Violet, fill: true);
        DrawRect(sb, 69, 47, 33, 10, White, fill: true);
        DrawLine(sb, 73, 50, 98, 50, Violet, 1.5);
        DrawLine(sb, 73, 54, 95, 54, Violet, 1.5);
        DrawLine(sb, 60, 94, 78, 101, Violet, 2.2);
        DrawLine(sb, 78, 101, 106, 87, Violet, 2.2);
    }

    private static void DrawGeometryTest(StringBuilder sb)
    {
        DrawRect(sb, 60, 450, 475, 185, Ink, fill: false, lineWidth: 1.2);
        DrawRect(sb, 86, 476, 423, 133, Muted, fill: false, lineWidth: 0.7, dashed: true);
        DrawLine(sb, 297.5, 450, 297.5, 635, Border, 0.55, dashed: true);
        DrawLine(sb, 60, 542.5, 535, 542.5, Border, 0.55, dashed: true);
        for (var i = 0; i <= 10; i++)
        {
            var x = 60 + (475d * i / 10d);
            var length = i % 5 == 0 ? 11 : 5;
            DrawLine(sb, x, 450, x, 450 + length, Ink, 0.65);
            DrawLine(sb, x, 635 - length, x, 635, Ink, 0.65);
        }
        DrawText(sb, "LEFT", 60, 648, 7, bold: true, Muted);
        DrawText(sb, "CENTRE", 297.5, 648, 7, bold: true, Muted, alignCenter: true);
        DrawText(sb, "RIGHT", 535, 648, 7, bold: true, Muted, alignRight: true);
    }

    private static void DrawGrayRamp(StringBuilder sb, double top)
    {
        const int count = 8;
        const double gap = 5;
        var width = (475 - (gap * (count - 1))) / count;
        for (var index = 0; index < count; index++)
        {
            var value = 1d - index / (double)(count - 1);
            var x = 60 + index * (width + gap);
            DrawRect(sb, x, top, width, 27, Gray(value), fill: true);
            DrawRect(sb, x, top, width, 27, Muted, fill: false, lineWidth: 0.45);
            DrawText(sb, $"{(int)Math.Round(value * 100)}%", x + width / 2, top + 40, 6, bold: false, Muted, alignCenter: true);
        }
    }

    private static void DrawColorRamp(StringBuilder sb, double top)
    {
        var colors = new[]
        {
            (R: 0.86, G: 0.12, B: 0.12), (R: 0.96, G: 0.58, B: 0.05),
            (R: 0.20, G: 0.58, B: 0.25), (R: 0.00, G: 0.57, B: 0.68),
            (R: 0.10, G: 0.30, B: 0.82), (R: 0.42, G: 0.22, B: 0.72),
            (R: 0.86, G: 0.20, B: 0.52), (R: 0.08, G: 0.09, B: 0.12)
        };
        const double gap = 5;
        var width = (475 - gap * (colors.Length - 1)) / colors.Length;
        for (var index = 0; index < colors.Length; index++)
        {
            var color = colors[index];
            DrawRect(sb, 60 + index * (width + gap), top, width, 24,
                new PdfColor(color.R, color.G, color.B), fill: true);
        }
    }

    private static void DrawRegistrationTarget(StringBuilder sb, double x, double top)
    {
        var y = DesignHeight - top;
        const double radius = 17;
        const double k = 0.55228475 * radius;
        sb.Append("q\n0.08 0.09 0.12 RG\n1.2 w\n")
            .Append(F(x + radius)).Append(' ').Append(F(y)).Append(" m ")
            .Append(F(x + radius)).Append(' ').Append(F(y + k)).Append(' ')
            .Append(F(x + k)).Append(' ').Append(F(y + radius)).Append(' ')
            .Append(F(x)).Append(' ').Append(F(y + radius)).Append(" c ")
            .Append(F(x - k)).Append(' ').Append(F(y + radius)).Append(' ')
            .Append(F(x - radius)).Append(' ').Append(F(y + k)).Append(' ')
            .Append(F(x - radius)).Append(' ').Append(F(y)).Append(" c ")
            .Append(F(x - radius)).Append(' ').Append(F(y - k)).Append(' ')
            .Append(F(x - k)).Append(' ').Append(F(y - radius)).Append(' ')
            .Append(F(x)).Append(' ').Append(F(y - radius)).Append(" c ")
            .Append(F(x + k)).Append(' ').Append(F(y - radius)).Append(' ')
            .Append(F(x + radius)).Append(' ').Append(F(y - k)).Append(' ')
            .Append(F(x + radius)).Append(' ').Append(F(y)).Append(" c S\n")
            .Append(F(x - 25)).Append(' ').Append(F(y)).Append(" m ")
            .Append(F(x + 25)).Append(' ').Append(F(y)).Append(" l S\n")
            .Append(F(x)).Append(' ').Append(F(y - 25)).Append(" m ")
            .Append(F(x)).Append(' ').Append(F(y + 25)).Append(" l S\nQ\n");
    }

    private static void DrawPanel(StringBuilder sb, double x, double top, double width, double height)
    {
        DrawRect(sb, x, top, width, height, Panel, fill: true);
        DrawRect(sb, x, top, width, height, Border, fill: false, lineWidth: 0.7);
    }

    private static void DrawLine(
        StringBuilder sb, double x1, double top1, double x2, double top2,
        PdfColor color, double lineWidth, bool dashed = false)
    {
        sb.Append("q\n").Append(ColorCommand(color, stroke: true))
            .Append(F(lineWidth)).Append(" w\n");
        if (dashed) sb.Append("[3 2] 0 d\n");
        sb.Append(F(x1)).Append(' ').Append(F(DesignHeight - top1)).Append(" m ")
            .Append(F(x2)).Append(' ').Append(F(DesignHeight - top2)).Append(" l S\nQ\n");
    }

    private static void DrawRect(
        StringBuilder sb, double x, double top, double width, double height,
        PdfColor color, bool fill, double lineWidth = 0.7, bool dashed = false)
    {
        sb.Append("q\n").Append(ColorCommand(color, stroke: !fill));
        if (!fill) sb.Append(F(lineWidth)).Append(" w\n");
        if (dashed) sb.Append("[3 2] 0 d\n");
        sb.Append(F(x)).Append(' ').Append(F(DesignHeight - top - height)).Append(' ')
            .Append(F(width)).Append(' ').Append(F(height)).Append(" re ")
            .Append(fill ? "f\n" : "S\n").Append("Q\n");
    }

    private static void DrawText(
        StringBuilder sb,
        string value,
        double x,
        double topBaseline,
        double size,
        bool bold,
        PdfColor color,
        bool alignCenter = false,
        bool alignRight = false)
    {
        var safe = PdfText(value);
        if (safe.Length == 0) return;
        var font = bold ? "F2" : "F1";
        var estimatedWidth = safe.Length * size * (bold ? 0.56 : 0.52);
        var drawX = alignCenter ? x - estimatedWidth / 2d : alignRight ? x - estimatedWidth : x;
        sb.Append("q\n").Append(ColorCommand(color, stroke: false))
            .Append("BT /").Append(font).Append(' ').Append(F(size)).Append(" Tf ")
            .Append(F(drawX)).Append(' ').Append(F(DesignHeight - topBaseline))
            .Append(" Td (").Append(EscapePdfString(safe)).Append(") Tj ET\nQ\n");
    }

    private static byte[] BuildPage(string pageContent, double scale, double offsetX, double offsetY)
    {
        var wrapped = $"q\n{F(scale)} 0 0 {F(scale)} {F(offsetX)} {F(offsetY)} cm\n{pageContent}\nQ\n";
        return Encoding.ASCII.GetBytes(wrapped);
    }

    private static byte[] AssemblePdf(IReadOnlyList<byte[]> pages, double pageWidth, double pageHeight)
    {
        var objectCount = 4 + pages.Count * 2;
        var offsets = new long[objectCount + 1];
        using var output = new MemoryStream();

        WriteAscii(output, "%PDF-1.4\n%SPDIAG\n");
        WriteObject(output, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count)
            .Select(index => $"{5 + index * 2} 0 R"));
        WriteObject(output, offsets, 2, $"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        WriteObject(output, offsets, 3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        WriteObject(output, offsets, 4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        for (var index = 0; index < pages.Count; index++)
        {
            var pageId = 5 + index * 2;
            var contentId = pageId + 1;
            WriteObject(output, offsets, pageId,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pageWidth)} {F(pageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>");
            offsets[contentId] = output.Position;
            WriteAscii(output, $"{contentId} 0 obj\n<< /Length {pages[index].Length} >>\nstream\n");
            output.Write(pages[index]);
            WriteAscii(output, "endstream\nendobj\n");
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

    private static string SafeLabel(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Not reported";
        var clean = new string(value.Trim().Select(ch =>
            ch is >= ' ' and <= '~' ? ch : '?').ToArray());
        return clean.Length <= maximumLength ? clean : clean[..maximumLength];
    }

    private static string PdfText(string? value) =>
        SafeLabel(value, 120);

    private static string EscapePdfString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private static string ColorCommand(PdfColor color, bool stroke) =>
        $"{F(color.R)} {F(color.G)} {F(color.B)} {(stroke ? "RG" : "rg")}\n";

    private static PdfColor Gray(double value) => new(value, value, value);
    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void WriteObject(Stream output, long[] offsets, int id, string body)
    {
        offsets[id] = output.Position;
        WriteAscii(output, $"{id} 0 obj\n{body}\nendobj\n");
    }

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));

    private readonly record struct PdfColor(double R, double G, double B);

    private static readonly PdfColor White = new(1, 1, 1);
    private static readonly PdfColor Ink = new(0.08, 0.10, 0.16);
    private static readonly PdfColor Violet = new(0.40, 0.28, 0.82);
    private static readonly PdfColor Muted = new(0.34, 0.38, 0.45);
    private static readonly PdfColor Border = new(0.78, 0.80, 0.85);
    private static readonly PdfColor Panel = new(0.965, 0.965, 0.98);
}