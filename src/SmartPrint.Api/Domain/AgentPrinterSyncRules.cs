using System.Text.Json;
using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public enum AgentPrinterSyncValidationFailure
{
    None,
    PrinterLimitExceeded
}

public sealed record AgentPrinterSyncValidationResult(
    AgentPrinterSyncValidationFailure Failure,
    IReadOnlyList<AgentPrinterDiscoveryRequest> Printers,
    int RejectedCount = 0,
    int CapabilitiesUnknownCount = 0);

/// <summary>
/// Validates discovered Windows queues independently. A single bad driver capability
/// report or malformed queue must not hide the other installed queues from the owner.
/// </summary>
public static class AgentPrinterSyncRules
{
    public const int MaximumPrinters = 100;
    public const int MaximumQueueNameLength = 160;
    public const int MaximumDriverNameLength = 160;
    public const int MaximumPortNameLength = 255;
    public const int MaximumCapabilitiesJsonLength = 16000;

    private static readonly HashSet<string> ValidStatuses =
        ["Ready", "Busy", "Offline", "Paused", "Error", "Unknown"];

    public static AgentPrinterSyncValidationResult Validate(
        IReadOnlyList<AgentPrinterDiscoveryRequest>? printers)
    {
        if (printers is null || printers.Count > MaximumPrinters)
        {
            return new AgentPrinterSyncValidationResult(
                AgentPrinterSyncValidationFailure.PrinterLimitExceeded,
                Array.Empty<AgentPrinterDiscoveryRequest>());
        }

        var normalized = new List<AgentPrinterDiscoveryRequest>(printers.Count);
        var queueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejectedCount = 0;
        var capabilitiesUnknownCount = 0;

        foreach (var printer in printers)
        {
            if (printer is null)
            {
                rejectedCount++;
                continue;
            }

            var queueName = printer.QueueName?.Trim();
            var driverName = printer.DriverName?.Trim();
            var portName = printer.PortName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(queueName) ||
                queueName.Length > MaximumQueueNameLength ||
                string.IsNullOrWhiteSpace(driverName) ||
                driverName.Length > MaximumDriverNameLength ||
                portName.Length > MaximumPortNameLength ||
                !ValidStatuses.Contains(printer.Status) ||
                printer.OutputMode is not ("PHYSICAL" or "PDF_EXPORT" or "UNSUPPORTED_VIRTUAL"))
            {
                rejectedCount++;
                continue;
            }

            if (!queueNames.Add(queueName))
            {
                rejectedCount++;
                continue;
            }

            var capabilitiesJson = printer.CapabilitiesJson;
            if (!HasValidCapabilitiesJson(capabilitiesJson))
            {
                // Queue identity is still useful: keep it visible and mark capabilities unknown.
                capabilitiesJson = "{}";
                capabilitiesUnknownCount++;
            }

            normalized.Add(printer with
            {
                QueueName = queueName,
                DriverName = driverName,
                PortName = portName,
                CapabilitiesJson = capabilitiesJson
            });
        }

        return new AgentPrinterSyncValidationResult(
            AgentPrinterSyncValidationFailure.None,
            normalized,
            rejectedCount,
            capabilitiesUnknownCount);
    }

    /// <summary>
    /// Decides whether a newly discovered queue can take an unused plan slot.
    /// Existing owner selections are preserved by the sync endpoint.
    /// </summary>
    public static bool ShouldAutoSelectQueue(
        string outputMode,
        string status,
        bool subscriptionActive,
        long selectedCount,
        int? maximum) =>
        subscriptionActive &&
        (outputMode is "PHYSICAL" or "PDF_EXPORT" or "UNSUPPORTED_VIRTUAL") &&
        (status is "Ready" or "Busy") &&
        (maximum is null || selectedCount < maximum.Value);

    public static string ResolveCapabilitiesJson(
        string? existing,
        string? discovered,
        bool driverChanged)
    {
        var discoveredKnown = HasKnownCapabilitiesJson(discovered);
        if (driverChanged)
            return discoveredKnown ? discovered! : "{}";

        if (discoveredKnown)
            return discovered!;

        return HasKnownCapabilitiesJson(existing) ? existing! : "{}";
    }

    private static bool HasKnownCapabilitiesJson(string? value) =>
        HasValidCapabilitiesJson(value) &&
        !string.Equals(value!.Trim(), "{}", StringComparison.Ordinal);

    private static bool HasValidCapabilitiesJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumCapabilitiesJsonLength)
            return false;

        try
        {
            using var parsed = JsonDocument.Parse(value);
            return parsed.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
