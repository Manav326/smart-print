using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public sealed record AgentHeartbeatResult(
    Guid Id,
    string Status,
    DateTimeOffset LastSeenUtc,
    string AgentVersion,
    IReadOnlyList<string> LocalEndpoints,
    string PublicBaseUrl,
    IReadOnlyList<string> AllowedPortalOrigins,
    int AgentCompletedDocumentRetentionDays,
    int AgentRetentionSweepHours);

public enum AgentPrinterSyncFailure
{
    None,
    PrinterLimitExceeded,
    InvalidCapabilitiesJson,
    InvalidPrinterMetadata,
    QuotaExceeded
}

public sealed record AgentPrinterSyncResult(
    AgentPrinterSyncFailure Failure,
    Guid? AgentDeviceId = null,
    int Discovered = 0,
    int Created = 0,
    int Updated = 0,
    int Unavailable = 0,
    DateTimeOffset? SyncedUtc = null,
    string? QuotaCode = null,
    string? QuotaMessage = null,
    long? QuotaCurrent = null,
    long? QuotaMaximum = null,
    int Rejected = 0,
    int CapabilitiesUnknown = 0,
    int Selected = 0,
    int? MaximumSelected = null)
{
    public bool Succeeded => Failure == AgentPrinterSyncFailure.None;
    public bool Partial => Rejected > 0 || CapabilitiesUnknown > 0;
}

public sealed class AgentConnectivityService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<AgentHeartbeatResult> HeartbeatAsync(
        AgentDevice device,
        AgentHeartbeatRequest request,
        string publicBaseUrl,
        IReadOnlyList<string> corsOrigins,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        var localEndpoints = AgentLocalEndpointRules.Normalize(request.LocalEndpoints);
        device.LocalEndpointsJson = JsonSerializer.Serialize(localEndpoints);
        device.Status = "Online";
        device.LastSeenUtc = now;
        await db.SaveChangesAsync(cancellationToken);

        var operationalSettings = await db.PlatformOperationalSettings.AsNoTracking()
            .SingleAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, cancellationToken);

        return new AgentHeartbeatResult(
            device.Id,
            device.Status,
            device.LastSeenUtc.Value,
            request.Version,
            localEndpoints,
            publicBaseUrl,
            corsOrigins,
            operationalSettings.AgentCompletedDocumentRetentionDays,
            operationalSettings.AgentRetentionSweepHours);
    }

    public async Task<AgentPrinterSyncResult> SyncPrintersAsync(
        AgentDevice device,
        AgentPrinterSyncRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);

        var validation = AgentPrinterSyncRules.Validate(request.Printers);
        if (validation.Failure != AgentPrinterSyncValidationFailure.None)
        {
            return new AgentPrinterSyncResult(
                validation.Failure == AgentPrinterSyncValidationFailure.PrinterLimitExceeded
                    ? AgentPrinterSyncFailure.PrinterLimitExceeded
                    : AgentPrinterSyncFailure.InvalidPrinterMetadata);
        }

        var normalized = validation.Printers;
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await entitlements.AcquireQuotaLockAsync(device.TenantId, TenantQuotaResource.Printer, cancellationToken);

        var entitlement = await entitlements.GetForTenantAsync(device.TenantId, cancellationToken);
        var selectedPrinterCount = await db.PrinterDevices.AsNoTracking()
            .LongCountAsync(printer => printer.TenantId == device.TenantId &&
                printer.IsActive && (printer.OutputMode == "PHYSICAL" || printer.OutputMode == "PDF_EXPORT"),
                cancellationToken);

        var existing = await db.PrinterDevices
            .Where(printer => printer.TenantId == device.TenantId &&
                              printer.AgentDeviceId == device.Id)
            .ToListAsync(cancellationToken);
        var seenQueues = normalized.Select(printer => printer.QueueName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        var updated = 0;
        var autoSelected = 0;

        foreach (var discovered in normalized)
        {
            var printer = existing.FirstOrDefault(item =>
                string.Equals(item.QueueName, discovered.QueueName, StringComparison.OrdinalIgnoreCase));

            var adoptedUnlinkedQueue = false;
            if (printer is null)
            {
                printer = await db.PrinterDevices.FirstOrDefaultAsync(item =>
                    item.TenantId == device.TenantId &&
                    item.QueueName == discovered.QueueName &&
                    (item.AgentDeviceId == null ||
                     db.AgentDevices.Any(previousAgent =>
                         previousAgent.Id == item.AgentDeviceId &&
                         previousAgent.TenantId == device.TenantId &&
                         previousAgent.RevokedUtc != null)), cancellationToken);
                adoptedUnlinkedQueue = printer is not null && printer.AgentDeviceId is null;
            }

            if (printer is null)
            {
                var selectQueue = AgentPrinterSyncRules.ShouldAutoSelectQueue(
                    discovered.OutputMode,
                    discovered.Status,
                    entitlement.IsAccessActive,
                    selectedPrinterCount,
                    entitlement.MaxPrinters);
                printer = new PrinterDevice
                {
                    TenantId = device.TenantId,
                    AgentDeviceId = device.Id,
                    DisplayName = discovered.QueueName.Trim(),
                    Manufacturer = "Windows",
                    Model = discovered.DriverName.Trim(),
                    ConnectionType = "WindowsQueue",
                    OutputMode = discovered.OutputMode,
                    ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim(),
                    QueueName = discovered.QueueName.Trim(),
                    IsDefault = discovered.IsDefault,
                    CapabilitiesJson = discovered.CapabilitiesJson,
                    IsActive = selectQueue,
                    CreatedUtc = now
                };
                db.PrinterDevices.Add(printer);
                if (selectQueue)
                {
                    selectedPrinterCount++;
                    autoSelected++;
                }
                created++;
            }
            else
            {
                var wasQuotaSelected = printer.IsActive &&
                    (printer.OutputMode == "PHYSICAL" || printer.OutputMode == "PDF_EXPORT");
                printer.AgentDeviceId = device.Id;
                printer.DisplayName = discovered.QueueName.Trim();
                printer.Manufacturer = "Windows";
                printer.Model = discovered.DriverName.Trim();
                printer.ConnectionType = "WindowsQueue";
                printer.OutputMode = discovered.OutputMode;
                printer.ConnectionAddress = string.IsNullOrWhiteSpace(discovered.PortName) ? null : discovered.PortName.Trim();
                printer.IsDefault = discovered.IsDefault;
                printer.CapabilitiesJson = discovered.CapabilitiesJson;

                if (discovered.OutputMode == "UNSUPPORTED_VIRTUAL")
                {
                    printer.IsActive = false;
                    if (wasQuotaSelected) selectedPrinterCount--;
                }
                else if (adoptedUnlinkedQueue && !printer.IsActive)
                {
                    var selectQueue = AgentPrinterSyncRules.ShouldAutoSelectQueue(
                        discovered.OutputMode,
                        discovered.Status,
                        entitlement.IsAccessActive,
                        selectedPrinterCount,
                        entitlement.MaxPrinters);
                    if (selectQueue)
                    {
                        printer.IsActive = true;
                        selectedPrinterCount++;
                        autoSelected++;
                    }
                }
                updated++;
            }

            printer.Status = discovered.Status;
            printer.LastSeenUtc = now;
            printer.UpdatedUtc = now;
        }

        var unavailable = 0;
        if (validation.RejectedCount == 0)
        {
            foreach (var printer in existing.Where(printer =>
                         !seenQueues.Contains(printer.QueueName ?? string.Empty)))
            {
                printer.Status = "Unavailable";
                printer.UpdatedUtc = now;
                unavailable++;
            }
        }

        device.Status = "Online";
        device.LastSeenUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AgentPrinterSyncResult(
            AgentPrinterSyncFailure.None,
            AgentDeviceId: device.Id,
            Discovered: normalized.Count,
            Created: created,
            Updated: updated,
            Unavailable: unavailable,
            SyncedUtc: now,
            Rejected: validation.RejectedCount,
            CapabilitiesUnknown: validation.CapabilitiesUnknownCount,
            Selected: checked((int)Math.Min(selectedPrinterCount, int.MaxValue)),
            MaximumSelected: entitlement.MaxPrinters);
    }
}
