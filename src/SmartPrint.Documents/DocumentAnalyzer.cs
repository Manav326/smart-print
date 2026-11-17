using System.Buffers.Binary;
using UglyToad.PdfPig;
using StbImageSharp;

namespace SmartPrint.Documents;

public sealed record DocumentAnalysis(
    int PageCount,
    string PageSizesJson,
    string MediaType,
    string Extension);

public sealed class DocumentValidationException(string message) : Exception(message);

/// <summary>
/// Performs the authoritative supported-format, page-count and image-integrity checks.
/// Stream-based callers must supply a seekable stream so PDF parsing never falls back
/// to copying an entire non-seekable network stream into memory.
/// </summary>
public static class DocumentAnalyzer
{
    private const int MaximumPages = 500;
    private const ulong MaximumImagePixels = 50_000_000UL;
    private const int MaximumSignatureSearchBytes = 1028;

    public static string? IdentifyExtension(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length >= pngSignature.Length && bytes[..pngSignature.Length].SequenceEqual(pngSignature))
            return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";

        var searchLength = Math.Min(bytes.Length, MaximumSignatureSearchBytes);
        var lastHeaderOffset = Math.Min(1023, searchLength - 5);
        for (var offset = 0; offset <= lastHeaderOffset; offset++)
        {
            if (bytes.Slice(offset, 5).SequenceEqual("%PDF-"u8))
                return ".pdf";
        }
        return null;
    }

    /// <summary>
    /// Compatibility entry point for existing callers. The memory stream wraps the
    /// supplied array without copying it; new ingestion and storage paths should use
    /// the seekable Stream overload directly.
    /// </summary>
    public static DocumentAnalysis Analyse(byte[] bytes, string filenameExtension = "")
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        return Analyse(stream, filenameExtension);
    }

    /// <summary>
    /// Analyses a seekable document stream without materialising the complete source
    /// file as another byte array. The input stream remains open and its original
    /// position is restored before returning.
    /// </summary>
    public static DocumentAnalysis Analyse(Stream stream, string filenameExtension = "")
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("Document analysis requires a readable, seekable stream.", nameof(stream));

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            var signature = ReadPrefix(stream, MaximumSignatureSearchBytes);
            var extension = IdentifyExtension(signature) ??
                throw new DocumentValidationException("The file content is not a supported PDF, JPEG or PNG document.");

            // Preserve the established .jpeg storage/ticket alias when it agrees
            // with the detected JPEG signature; all other mismatches are ignored.
            if (extension == ".jpg" &&
                string.Equals(filenameExtension, ".jpeg", StringComparison.OrdinalIgnoreCase))
                extension = ".jpeg";

            return extension switch
            {
                ".pdf" => AnalysePdf(stream),
                ".png" => AnalysePng(stream),
                ".jpg" or ".jpeg" => AnalyseJpeg(stream, extension),
                _ => throw new DocumentValidationException("The file content is not a supported PDF, JPEG or PNG document.")
            };
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static DocumentAnalysis AnalysePdf(Stream stream)
    {
        if (stream.Length < 8)
            throw new DocumentValidationException("The PDF file is incomplete.");

        try
        {
            stream.Position = 0;
            using var document = PdfDocument.Open(stream);
            if (document.NumberOfPages is <= 0 or > MaximumPages)
            {
                throw new DocumentValidationException($"PDFs must contain between 1 and {MaximumPages} pages.");
            }

            var pages = document.GetPages().Select(page =>
            {
                var images = page.GetImages().Select(image =>
                {
                    var box = image.BoundingBox;
                    return new
                    {
                        widthInSamples = image.WidthInSamples,
                        heightInSamples = image.HeightInSamples,
                        leftPoints = Math.Round(box.Left, 2),
                        bottomPoints = Math.Round(box.Bottom, 2),
                        rightPoints = Math.Round(box.Right, 2),
                        topPoints = Math.Round(box.Top, 2),
                        rotationDegrees = Math.Round(box.Rotation, 2),
                        isImageMask = image.IsImageMask
                    };
                }).ToArray();

                return new
                {
                    widthPoints = Math.Round(page.Width, 2),
                    heightPoints = Math.Round(page.Height, 2),
                    orientation = page.Width > page.Height ? "Landscape" : page.Width < page.Height ? "Portrait" : "Square",
                    images
                };
            }).ToArray();

            return new DocumentAnalysis(
                document.NumberOfPages,
                System.Text.Json.JsonSerializer.Serialize(pages),
                "application/pdf",
                ".pdf");
        }
        catch (DocumentValidationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new DocumentValidationException("This PDF is damaged, password-protected or could not be analysed.");
        }
    }

    private static DocumentAnalysis AnalysePng(Stream stream)
    {
        Span<byte> header = stackalloc byte[24];
        stream.Position = 0;
        if (!TryReadExactly(stream, header) ||
            !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new DocumentValidationException("The PNG file signature is invalid.");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        ValidateImageDimensions(width, height);
        return DecodeAndAnalyseImage(stream, width, height, "image/png", ".png");
    }

    private static DocumentAnalysis AnalyseJpeg(Stream stream, string extension)
    {
        stream.Position = 0;
        Span<byte> signature = stackalloc byte[3];
        if (!TryReadExactly(stream, signature) ||
            signature[0] != 0xFF || signature[1] != 0xD8 || signature[2] != 0xFF)
        {
            throw new DocumentValidationException("The JPEG file signature is invalid.");
        }

        var dimensions = ReadJpegDimensions(stream);
        ValidateImageDimensions(dimensions.Width, dimensions.Height);
        return DecodeAndAnalyseImage(stream, dimensions.Width, dimensions.Height, "image/jpeg", extension);
    }

    private static (uint Width, uint Height) ReadJpegDimensions(Stream stream)
    {
        stream.Position = 2;
        while (stream.Position + 4 <= stream.Length)
        {
            var prefix = stream.ReadByte();
            if (prefix < 0) break;
            if (prefix != 0xFF) continue;

            int marker;
            do
            {
                marker = stream.ReadByte();
            } while (marker == 0xFF);

            if (marker < 0) break;
            if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7 or 0x01)
                continue;
            if (marker == 0xDA)
                break; // Start of scan arrived before a supported frame header.

            var lengthHigh = stream.ReadByte();
            var lengthLow = stream.ReadByte();
            if (lengthHigh < 0 || lengthLow < 0)
                break;

            var segmentLength = (lengthHigh << 8) | lengthLow;
            if (segmentLength < 2 || stream.Position + segmentLength - 2 > stream.Length)
                break;

            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
                0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (segmentLength < 7)
                    break;

                _ = stream.ReadByte(); // Sample precision.
                var heightHigh = stream.ReadByte();
                var heightLow = stream.ReadByte();
                var widthHigh = stream.ReadByte();
                var widthLow = stream.ReadByte();
                if (heightHigh < 0 || heightLow < 0 || widthHigh < 0 || widthLow < 0)
                    break;

                return (
                    (uint)((widthHigh << 8) | widthLow),
                    (uint)((heightHigh << 8) | heightLow));
            }

            stream.Position += segmentLength - 2;
        }

        throw new DocumentValidationException("The JPEG image dimensions could not be read.");
    }

    private static DocumentAnalysis DecodeAndAnalyseImage(
        Stream stream,
        uint expectedWidth,
        uint expectedHeight,
        string mediaType,
        string extension)
    {
        try
        {
            // Header inspection is inexpensive; a full decode proves that compressed
            // pixel data is complete. Decode directly from the seekable source stream
            // instead of first copying the compressed document into a byte array.
            stream.Position = 0;
            var imageInfo = ImageInfo.FromStream(stream);
            if (imageInfo is null)
                throw new DocumentValidationException("The image format could not be identified.");
            var identified = imageInfo.Value;

            if (identified.Width != expectedWidth || identified.Height != expectedHeight)
                throw new DocumentValidationException("The image dimensions do not match their file header.");

            stream.Position = 0;
            var decoded = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            var expectedDecodedBytes = checked((long)identified.Width * identified.Height * 4);
            if (decoded.Width != identified.Width || decoded.Height != identified.Height ||
                decoded.Data.LongLength != expectedDecodedBytes)
                throw new DocumentValidationException("The image pixel data is incomplete or invalid.");

            return new DocumentAnalysis(
                1,
                ImagePageSize((uint)decoded.Width, (uint)decoded.Height),
                mediaType,
                extension);
        }
        catch (DocumentValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new DocumentValidationException("The image is damaged, incomplete or could not be fully decoded.");
        }
    }

    private static void ValidateImageDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || (ulong)width * height > MaximumImagePixels)
        {
            throw new DocumentValidationException("The image dimensions are invalid or too large to process safely.");
        }
    }

    private static string ImagePageSize(uint width, uint height) =>
        System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { pixelWidth = width, pixelHeight = height, orientation = width > height ? "Landscape" : width < height ? "Portrait" : "Square" }
        });

    private static byte[] ReadPrefix(Stream stream, int maximumBytes)
    {
        var buffer = new byte[maximumBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }

        if (total == buffer.Length) return buffer;
        Array.Resize(ref buffer, total);
        return buffer;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0) return false;
            total += read;
        }
        return true;
    }
}
