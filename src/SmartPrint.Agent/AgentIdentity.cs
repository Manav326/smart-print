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
        "pdf", "xps", "onenote", "fax", "virtual", "document writer", "print to file"
    ];

    public static bool IsPdfExport(string? queueName, string? driverName = null) =>
        string.Equals(queueName?.Trim(), "Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(driverName?.Trim(), "Microsoft Print To PDF", StringComparison.OrdinalIgnoreCase);

    public static string Detect(string? queueName, string? driverName, string? portName = null)
    {
        if (IsPdfExport(queueName, driverName)) return PdfExport;

        var identity = $"{queueName} {driverName}";
        if (UnsupportedVirtualIndicators.Any(indicator =>
                identity.Contains(indicator, StringComparison.OrdinalIgnoreCase)) ||
            (portName?.Contains("PORTPROMPT:", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (portName?.Contains("FILE:", StringComparison.OrdinalIgnoreCase) ?? false))
            return UnsupportedVirtual;

        return Physical;
    }
}

public sealed record AgentHeartbeatResponse(
    string? PublicBaseUrl,
    IReadOnlyList<string>? AllowedPortalOrigins = null,
    int? AgentCompletedDocumentRetentionDays = null,
    int? AgentRetentionSweepHours = null);
