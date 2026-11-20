namespace SmartPrint.Agent;

public sealed record AgentCredentials(
    string ApiBaseUrl,
    Guid AgentDeviceId,
    string AgentSecret,
    DateTimeOffset PairedUtc);

public sealed record AgentEnrollmentRequest(
    string Code,
    string Fingerprint,
    string DeviceName);

public sealed record AgentEnrollmentResponse(
    Guid AgentDeviceId,
    string AgentSecret,
    DateTimeOffset CreatedUtc);

public sealed record DiscoveredPrinter(
    string QueueName,
    string DriverName,
    string PortName,
    string Status,
    bool IsDefault,
    string OutputMode = PrinterOutputModeRules.Physical,
    string CapabilitiesJson = "{}");

public static class PrinterOutputModeRules
{
    public const string Physical = "PHYSICAL";
    public const string PdfExport = "PDF_EXPORT";
    public const string UnsupportedVirtual = "UNSUPPORTED_VIRTUAL";

    private static readonly string[] UnsupportedVirtualIndicators =
    [
        "pdf", "xps", "onenote", "fax", "document writer", "print to file"
    ];

    public static bool IsPdfExport(string? queueName, string? driverName = null) =>
        string.Equals(queueName?.Trim(), "Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(driverName?.Trim(), "Microsoft Print To PDF", StringComparison.OrdinalIgnoreCase);

    public static string Detect(string? queueName, string? driverName, string? portName = null)
    {
        if (IsPdfExport(queueName, driverName)) return PdfExport;

        var identity = $"{queueName} {driverName}";
        var isFilePort =
            (portName?.Contains("PORTPROMPT:", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (portName?.Contains("FILE:", StringComparison.OrdinalIgnoreCase) ?? false);
        if (isFilePort || UnsupportedVirtualIndicators.Any(indicator =>
                identity.Contains(indicator, StringComparison.OrdinalIgnoreCase)))
            return UnsupportedVirtual;

        // "IppVirtual" is a common queue name for an IPP endpoint, not a
        // file-generating virtual printer. Only treat a standalone "virtual"
        // token as unsupported when Windows has not exposed an IPP/network
        // printing path or an IPP class driver.
        var isIppQueue =
            (portName?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (portName?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (portName?.StartsWith("ipp://", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (portName?.StartsWith("ipps://", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (driverName?.Contains("IPP", StringComparison.OrdinalIgnoreCase) ?? false);
        var hasStandaloneVirtualToken = identity.Split(
                [' ', '\t', '-', '_', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Equals("virtual", StringComparison.OrdinalIgnoreCase));
        if (hasStandaloneVirtualToken && !isIppQueue)
            return UnsupportedVirtual;

        return Physical;
    }
}

public sealed record AgentHeartbeatResponse(
    string? PublicBaseUrl,
    IReadOnlyList<string>? AllowedPortalOrigins = null,
    int? AgentCompletedDocumentRetentionDays = null,
    int? AgentRetentionSweepHours = null);
