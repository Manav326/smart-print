namespace SmartPrint.Api.Domain;

public sealed record NormalizedPrintJobSettings(
    string ColourMode,
    string PaperSize,
    bool IsDuplex,
    int Copies,
    string Orientation);

public static class PrintJobSettingsRules
{
    private static readonly string[] SupportedPaperSizes = ["A4", "A3", "A5", "LETTER", "LEGAL"];
    private static readonly string[] SupportedOrientations = ["AUTO", "PORTRAIT", "LANDSCAPE"];

    public static bool TryNormalize(
        string? colourMode,
        string? paperSize,
        bool isDuplex,
        int copies,
        string? orientation,
        out NormalizedPrintJobSettings settings)
    {
        var normalizedColourMode = colourMode?.Trim();
        var normalizedPaperSize = paperSize?.Trim().ToUpperInvariant();
        var normalizedOrientation = orientation?.Trim().ToUpperInvariant();

        if (normalizedColourMode is not ("Mono" or "Colour") ||
            normalizedPaperSize is null || !SupportedPaperSizes.Contains(normalizedPaperSize, StringComparer.Ordinal) ||
            copies is < 1 or > 100 ||
            normalizedOrientation is null || !SupportedOrientations.Contains(normalizedOrientation, StringComparer.Ordinal))
        {
            settings = null!;
            return false;
        }

        settings = new NormalizedPrintJobSettings(
            normalizedColourMode,
            normalizedPaperSize,
            isDuplex,
            copies,
            normalizedOrientation);
        return true;
    }

    public static bool CanRevise(string? printKind, string? status) =>
        printKind == "DOCUMENT" &&
        status is "AWAITING_CONFIGURATION" or "AWAITING_CUSTOMER_APPROVAL" or
            "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "QUOTE_EXPIRED" or "PAYMENT_REJECTED";
}
