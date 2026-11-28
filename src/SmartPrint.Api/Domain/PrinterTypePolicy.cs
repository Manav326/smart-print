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

        // These Windows ports are file/prompt sinks, not connected print targets.
        return !port.Equals("FILE:", StringComparison.OrdinalIgnoreCase) &&
               !port.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase) &&
               !port.Equals("NUL:", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveConnectionType(
        string? portName,
        string? driverName = null,
        string? queueName = null)
    {
        var value = portName?.Trim() ?? string.Empty;
        var queue = queueName?.Trim() ?? string.Empty;
        var usesIppDriver = driverName?.Contains("IPP", StringComparison.OrdinalIgnoreCase) == true;

        // A UNC-connected queue is presented through a print-server share,
        // even when the server-side printer itself uses IPP or USB.
        if (queue.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            return "Shared";

        if (Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
        {
            if (endpoint.Scheme is "ipp" or "ipps")
                return "IPP";

            // HTTP(S) alone is not proof of IPP. Require the installed IPP driver.
            if (endpoint.Scheme is "http" or "https")
                return usesIppDriver ? "IPP" : "LAN";
        }

        // Some USB-connected Windows drivers expose the DOT4 port monitor
        // instead of USB00x. It is still a USB-attached queue.
        if (value.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("DOT4_", StringComparison.OrdinalIgnoreCase))
            return "USB";

        // Windows may hide the IPP URI behind a generated WSD port.
        if (usesIppDriver && value.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase))
            return "IPP";

        if (value.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("TCP_", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("LPR_", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("WSD", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("LPR", StringComparison.OrdinalIgnoreCase))
            return "LAN";

        return "WindowsQueue";
    }

    public static bool IsSelectableOutputMode(string? outputMode) =>
        string.Equals(outputMode, Physical, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase);

    public static bool UsesWindowsQueueReadiness(string? outputMode) =>
        !string.Equals(outputMode, PdfExport, StringComparison.OrdinalIgnoreCase);
}
