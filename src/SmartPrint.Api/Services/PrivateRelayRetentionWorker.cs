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
    }
}
