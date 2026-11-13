using System.Text.Json;
using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public enum AgentPrinterSyncValidationFailure
{
    None,
    PrinterLimitExceeded,
    InvalidCapabilitiesJson,
    InvalidPrinterMetadata
}

public sealed record AgentPrinterSyncValidationResult(
    AgentPrinterSyncValidationFailure Failure,
    IReadOnlyList<AgentPrinterDiscoveryRequest> Printers);

/// <summary>
/// Applies the same bounds and metadata checks used by the public Agent printer-sync endpoint
/// before a discovered queue set is persisted.
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
            return Invalid(AgentPrinterSyncValidationFailure.PrinterLimitExceeded);
        }

        if (printers.Any(printer =>
                printer is null || !HasValidCapabilitiesJson(printer.CapabilitiesJson)))
        {
            return Invalid(AgentPrinterSyncValidationFailure.InvalidCapabilitiesJson);
        }

        var normalized = printers
            .Where(printer =>
                !string.IsNullOrWhiteSpace(printer.QueueName) &&
                printer.QueueName.Length <= MaximumQueueNameLength &&
                !string.IsNullOrWhiteSpace(printer.DriverName) &&
                printer.DriverName.Length <= MaximumDriverNameLength &&
                (printer.OutputMode is "PHYSICAL" or "PDF_EXPORT" or "UNSUPPORTED_VIRTUAL") &&
                (printer.PortName?.Length ?? 0) <= MaximumPortNameLength &&
                (printer.CapabilitiesJson?.Length ?? 0) <= MaximumCapabilitiesJsonLength &&
                ValidStatuses.Contains(printer.Status))
            .GroupBy(printer => printer.QueueName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (normalized.Count != printers.Count)
        {
            return Invalid(AgentPrinterSyncValidationFailure.InvalidPrinterMetadata);
        }

        return new AgentPrinterSyncValidationResult(
            AgentPrinterSyncValidationFailure.None,
            normalized);
    }

    private static bool HasValidCapabilitiesJson(string? value)
    {
        var json = string.IsNullOrWhiteSpace(value) ? "{}" : value;
        if (json.Length > MaximumCapabilitiesJsonLength) return false;

        try
        {
            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AgentPrinterSyncValidationResult Invalid(
        AgentPrinterSyncValidationFailure failure) =>
        new(failure, Array.Empty<AgentPrinterDiscoveryRequest>());
}
