using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api.Services;

/// <summary>
/// Removes only opaque private-relay ciphertext after the paired Agent has
/// staged a verified local copy, or after an unclaimed relay expires/fails.
/// </summary>
public sealed class PrivateRelayRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PrivateRelayRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Private relay retention cleanup failed; encrypted relay copies remain protected and will be retried.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RunSweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<DocumentStorage>();
        var now = DateTimeOffset.UtcNow;

        var tickets = await db.LocalPrintUploadTickets
            .Where(ticket => ticket.PrivatePrinting &&
                ticket.PrivateRelayStorageKey != null &&
                (ticket.CompletedUtc != null ||
                 ticket.PrivateRelayFailedUtc != null ||
                 ticket.ExpiresUtc <= now))
            .OrderBy(ticket => ticket.PrivateRelayUploadedUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var ticket in tickets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(ticket.PrivateRelayStorageKey)) continue;
            try
            {
                await storage.DeleteAsync(ticket.PrivateRelayStorageKey);
                ticket.PrivateRelayStorageKey = null;
                ticket.PrivateRelayWrappedKey = null;
                ticket.PrivateRelayIv = null;
                ticket.PrivateRelayUploadedUtc = null;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException exception)
            {
                db.Entry(ticket).State = EntityState.Detached;
                logger.LogWarning(exception, "Could not delete expired/consumed private relay ticket {TicketId}; it will be retried.", ticket.Id);
            }
        }

        await SweepOrphanedRelayObjectsAsync(db, storage, cancellationToken);
    }

    private async Task SweepOrphanedRelayObjectsAsync(
        SmartPrintDbContext db,
        DocumentStorage storage,
        CancellationToken cancellationToken)
    {
        // A one-hour grace period protects an upload whose object has been written
        // but whose database transaction is still in progress. Objects younger than
        // this are never candidates, even when no ticket reference is visible yet.
        var candidates = storage.GetStalePrivateRelayStorageKeys(TimeSpan.FromHours(1), 250);
        if (candidates.Count == 0) return;

        var referencedKeys = await db.LocalPrintUploadTickets.AsNoTracking()
            .Where(ticket => ticket.PrivateRelayStorageKey != null &&
                             candidates.Contains(ticket.PrivateRelayStorageKey!))
            .Select(ticket => ticket.PrivateRelayStorageKey!)
            .ToListAsync(cancellationToken);
        var referenced = referencedKeys.ToHashSet(StringComparer.Ordinal);

        // A ticket can exist before its storage-key update commits. Do not sweep
        // any object belonging to a still-live ticket, even if its reference is
        // momentarily absent during a long-running upload transaction.
        var candidateTicketIds = candidates
            .Select(key => Guid.ParseExact(key.Split('/')[2], "N"))
            .Distinct()
            .ToArray();
        var activeTicketIds = await db.LocalPrintUploadTickets.AsNoTracking()
            .Where(ticket => candidateTicketIds.Contains(ticket.Id) &&
                             ticket.CompletedUtc == null &&
                             ticket.PrivateRelayFailedUtc == null &&
                             ticket.ExpiresUtc > DateTimeOffset.UtcNow)
            .Select(ticket => ticket.Id)
            .ToHashSetAsync(cancellationToken);

        foreach (var storageKey in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ticketId = Guid.ParseExact(storageKey.Split('/')[2], "N");
            if (referenced.Contains(storageKey) || activeTicketIds.Contains(ticketId)) continue;

            try
            {
                // This folder contains only encrypted private-relay payloads.
                // Never delete an object while any ticket still references it.
                await storage.DeleteAsync(storageKey);
                logger.LogInformation(
                    "Removed unreferenced private relay object after the orphan grace period: {StorageKey}.",
                    storageKey);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception,
                    "Could not remove unreferenced private relay object {StorageKey}; it will be retried.",
                    storageKey);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception,
                    "Private relay object {StorageKey} could not be removed because storage access was denied; it will be retried.",
                    storageKey);
            }
        }
    }
}
