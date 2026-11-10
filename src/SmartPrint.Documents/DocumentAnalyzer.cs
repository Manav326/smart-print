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

public static class DocumentAnalyzer
{
    private const int MaximumPages = 500;
    private const ulong MaximumImagePixels = 50_000_000UL;

    public static DocumentAnalysis Analyse(byte[] bytes, string extension)
    {
        if (extension == ".pdf")
        {
            if (bytes.Length < 8 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            {
                throw new DocumentValidationException("The file extension says PDF, but the file signature is invalid.");
            }

            try
            {
                using var document = PdfDocument.Open(new MemoryStream(bytes, writable: false));
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

                if (document.NumberOfPages is <= 0 or > MaximumPages)
                {
                    throw new DocumentValidationException($"PDFs must contain between 1 and {MaximumPages} pages.");
                }
                return new DocumentAnalysis(document.NumberOfPages, System.Text.Json.JsonSerializer.Serialize(pages), "application/pdf", ".pdf");
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

        if (extension == ".png")
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
                !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            {
                throw new DocumentValidationException("The PNG file signature is invalid.");
            }

            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            ValidateImageDimensions(width, height);
            return DecodeAndAnalyseImage(bytes, width, height, "image/png", ".png");
        }

        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
        {
            throw new DocumentValidationException("The JPEG file signature is invalid.");
        }

        var dimensions = ReadJpegDimensions(bytes);
        ValidateImageDimensions(dimensions.Width, dimensions.Height);
        return DecodeAndAnalyseImage(bytes, dimensions.Width, dimensions.Height, "image/jpeg", extension);
    }

    private static (uint Width, uint Height) ReadJpegDimensions(byte[] bytes)
    {
        var offset = 2;
        while (offset + 4 < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7 or 0x01) continue;
            if (offset + 2 > bytes.Length) break;
            var length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length) break;

            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (length < 7) break;
                var height = (uint)((bytes[offset + 3] << 8) | bytes[offset + 4]);
                var width = (uint)((bytes[offset + 5] << 8) | bytes[offset + 6]);
                return (width, height);
            }
            offset += length;
        }
        throw new DocumentValidationException("The JPEG image dimensions could not be read.");
    }

    private static DocumentAnalysis DecodeAndAnalyseImage(
        byte[] bytes, uint expectedWidth, uint expectedHeight, string mediaType, string extension)
    {
        try
        {
            // Header checks are cheap, but cannot prove that compressed pixel data
            // is complete. Decode the entire single-frame image before it becomes
            // a payable print job so truncated/corrupt files cannot pass preflight.
            using var infoStream = new MemoryStream(bytes, writable: false);
            var imageInfo = ImageInfo.FromStream(infoStream);
            if (imageInfo is null)
                throw new DocumentValidationException("The image format could not be identified.");
            var identified = imageInfo.Value;

            if (identified.Width != expectedWidth || identified.Height != expectedHeight)
                throw new DocumentValidationException("The image dimensions do not match its file header.");

            var decoded = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
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
}
