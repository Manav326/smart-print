using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Modules.AgentPrivateDocuments.Services;

/// <summary>
/// Reads the durable private-relay queue and waits for Agent-specific wake-up signals.
/// PostgreSQL remains the source of truth; notifications only reduce polling latency.
/// </summary>
public sealed class AgentPrivateRelayService(
    SmartPrintDbContext db,
    PrivateRelayWakeupService relayWakeups)
{
    public async Task<AgentPrivateRelayPendingResponse> GetPendingAsync(
        AgentDevice device,
        IHostEnvironment environment,
        int? waitSeconds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(environment);

        var allowPrivateVirtualOutput = !environment.IsProduction() &&
            await db.PlatformOperationalSettings.AsNoTracking()
                .Where(item => item.Id == SmartPrint.Api.Domain.PlatformOperationalSettings.SingletonIdValue)
                .Select(item => (bool?)item.AllowPrivatePrintingVirtualOutput)
                .SingleOrDefaultAsync(ct) is true;

        async Task<List<AgentPrivateRelayItem>> ReadPendingItemsAsync()
        {
            var now = DateTimeOffset.UtcNow;
            return await (
                from ticket in db.LocalPrintUploadTickets.AsNoTracking()
                join printer in db.PrinterDevices.AsNoTracking()
                    on new { ticket.TenantId, PrinterId = ticket.PrinterDeviceId }
                    equals new { printer.TenantId, PrinterId = printer.Id }
                where ticket.TenantId == device.TenantId &&
                      ticket.AgentDeviceId == device.Id &&
                      ticket.PrivatePrinting &&
                      ticket.PrivateRelayUploadedUtc != null &&
                      ticket.PrivateRelayStorageKey != null &&
                      ticket.CompletedUtc == null &&
                      ticket.PrivateRelayFailedUtc == null &&
                      ticket.ExpiresUtc > now &&
                      printer.AgentDeviceId == device.Id &&
                      printer.IsActive &&
                      (printer.OutputMode == PrivatePrintingOutputPolicy.Physical ||
                       (allowPrivateVirtualOutput && printer.OutputMode == PrivatePrintingOutputPolicy.PdfExport)) &&
                      printer.LastSeenUtc != null && printer.LastSeenUtc >= now.AddMinutes(-2) &&
                      (printer.Status == "Ready" || printer.Status == "Busy") &&
                      printer.QueueName != null && printer.QueueName != ""
                orderby ticket.PrivateRelayUploadedUtc
                select new AgentPrivateRelayItem(
                    ticket.Id, ticket.PrintJobId, ticket.JobNumber, ticket.PrinterDeviceId,
                    printer.DisplayName, printer.QueueName!, ticket.FileExtension, ticket.MediaType,
                    ticket.SizeBytes, ticket.Sha256, ticket.ColourMode, ticket.PaperSize,
                    ticket.IsDuplex, ticket.Copies, ticket.Orientation, ticket.ExpiresUtc,
                    ticket.PrintKind, printer.OutputMode, ticket.PagesPerSheet,
                    ticket.PhotoPaperFinish, ticket.PhotoOutputLayout, ticket.PhotoSizeCode))
                .Take(50)
                .ToListAsync(ct);
        }

        // Subscribe before reading the durable queue. This closes the race where a relay
        // arrives between the empty query and the wait. Notifications only wake the Agent;
        // PostgreSQL remains the source of truth and the bounded timeout is the fallback.
        var signal = relayWakeups.Subscribe(device.Id);
        var items = await ReadPendingItemsAsync();
        if (items.Count == 0)
        {
            // Keep fallback pickup responsive if LISTEN is unavailable behind a proxy.
            // With a live listener, notifications normally return immediately.
            var maximumWaitSeconds = relayWakeups.IsListening ? 25 : 10;
            var boundedWaitSeconds = Math.Clamp(waitSeconds ?? maximumWaitSeconds, 0, maximumWaitSeconds);
            await relayWakeups.WaitForSignalAsync(
                signal, TimeSpan.FromSeconds(boundedWaitSeconds), ct);
            items = await ReadPendingItemsAsync();
        }

        return new AgentPrivateRelayPendingResponse(items);
    }
}
