namespace SmartPrint.Api.Domain;

public static class PrinterTypePolicy
{
    public const string Physical = "PHYSICAL";
    public const string PdfExport = "PDF_EXPORT";
    public const string UnsupportedVirtual = "UNSUPPORTED_VIRTUAL";

    public static bool IsWorkingWindowsQueue(string? status) =>
        string.Equals(status, "Ready", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Busy", StringComparison.OrdinalIgnoreCase);

    public static string ResolveOutputMode(string? discoveredOutputMode) =>
        string.Equals(discoveredOutputMode, PdfExport, StringComparison.OrdinalIgnoreCase)
            ? PdfExport
            : string.Equals(discoveredOutputMode, Physical, StringComparison.OrdinalIgnoreCase)
                ? Physical
                : UnsupportedVirtual;

    public static bool IsConnectedPrinterPort(string? connectionPort)
    {
        var port = connectionPort?.Trim();
        if (string.IsNullOrWhiteSpace(port))
            return false;

        return !port.Equals("FILE:", StringComparison.OrdinalIgnoreCase) &&
               !port.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase) &&
               !port.Equals("NUL:", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveConnectionType(string? portName)
    {
        var value = portName?.Trim() ?? string.Empty;
        if (Uri.TryCreate(value, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme is "ipp" or "ipps" or "http" or "https")
            return "IPP";

        if (value.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
            return "USB";

        if (value.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("WSD", StringComparison.OrdinalIgnoreCase))
            return "LAN";

        return "WindowsQueue";
    }

    public static bool IsSelectableOutputMode(string? outputMode) =>
        string.Equals(outputMode, Physical, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outputMode, UnsupportedVirtual, StringComparison.OrdinalIgnoreCase);

    public static bool UsesWindowsQueueReadiness(string? outputMode) =>
        !string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase);
}
