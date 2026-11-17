using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SmartPrint.Agent;

internal static class PdfExportPreviewRules
{
    private const long MaximumPreviewSizeBytes = 100L * 1024 * 1024;

    internal static bool TryGetVerifiedReceipt(
        AgentLocalQueueEntry entry,
        string authorizedFileName,
        long authorizedSizeBytes,
        string authorizedSha256,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (entry.OutputMode != PrinterOutputModeRules.PdfExport ||
            entry.State is not ("PDF_EXPORTED" or "PDF_EXPORT_REPORTED") ||
            string.IsNullOrWhiteSpace(entry.OutputFilePath) ||
            string.IsNullOrWhiteSpace(entry.OutputFileName) ||
            entry.OutputFileSizeBytes is not long actualSize ||
            actualSize <= 0 || actualSize > MaximumPreviewSizeBytes ||
            actualSize != authorizedSizeBytes ||
            !string.Equals(entry.OutputFileName, authorizedFileName, StringComparison.Ordinal) ||
            !IsSha256(entry.OutputFileSha256) ||
            !IsSha256(authorizedSha256) ||
            !string.Equals(entry.OutputFileSha256, authorizedSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(entry.OutputFileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var candidate = Path.GetFullPath(entry.OutputFilePath);
            if (!Path.IsPathFullyQualified(candidate) ||
                !string.Equals(Path.GetFileName(candidate), entry.OutputFileName, StringComparison.Ordinal))
                return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool MatchesPdfBytes(byte[] bytes, long expectedSizeBytes, string expectedSha256)
    {
        if (bytes.Length < 100 || bytes.LongLength != expectedSizeBytes ||
            bytes.LongLength > MaximumPreviewSizeBytes ||
            !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            return false;

        var tailLength = Math.Min(bytes.Length, 8192);
        var tail = Encoding.ASCII.GetString(bytes, bytes.Length - tailLength, tailLength);
        if (!tail.Contains("%%EOF", StringComparison.Ordinal) || !IsSha256(expectedSha256))
            return false;

        try
        {
            var expectedHash = Convert.FromHexString(expectedSha256);
            var actualHash = SHA256.HashData(bytes);
            try { return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash); }
            finally
            {
                CryptographicOperations.ZeroMemory(actualHash);
                CryptographicOperations.ZeroMemory(expectedHash);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
