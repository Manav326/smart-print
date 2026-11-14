using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api.Services;

public sealed class DocumentRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<DocumentRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweepIntervalHours = 24;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                sweepIntervalHours = await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The document retention sweep failed; it will be retried at the next interval.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(Math.Clamp(sweepIntervalHours, 1, 168)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<int> RunSweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartPrintDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<DocumentStorage>();
        var settings = await db.PlatformOperationalSettings.AsNoTracking()
            .SingleAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var completedCutoff = now.AddDays(-settings.CloudCompletedDocumentRetentionDays);
        var abandonedCutoff = now.AddDays(-settings.AbandonedDocumentRetentionDays);
        var purgedCount = 0;

        for (var batch = 0; batch < 10; batch++)
        {
            var candidates = await (
                from document in db.PrintDocuments
                join job in db.PrintJobs
                    on new { document.TenantId, PrintJobId = document.PrintJobId }
                    equals new { job.TenantId, PrintJobId = job.Id }
                let completedUtc = job.OutputType == "PDF_EXPORT" ? job.OutputCompletedUtc : job.OutputConfirmedUtc
                let hasFinancialHold = db.PrintPayments.Any(payment =>
                    payment.TenantId == job.TenantId && payment.PrintJobId == job.Id &&
                    (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                     payment.Status == "CUSTOMER_REPORTED" ||
                     payment.Status == "CASH_AWAITING_PAYMENT" ||
                     ((payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                      payment.RefundedAmountMinor < payment.AmountMinor)))
                where document.StorageMode == "CLOUD" &&
                      document.PurgedUtc == null &&
                      ((job.Status == "COMPLETED" && completedUtc != null && completedUtc <= completedCutoff) ||
                       ((job.Status == "REJECTED" || job.Status == "PAYMENT_REJECTED" || job.Status == "QUOTE_EXPIRED" || job.Status == "REFUNDED") &&
                        job.OutputConfirmedUtc == null && job.OutputCompletedUtc == null &&
                        job.UpdatedUtc <= abandonedCutoff && !hasFinancialHold))
                orderby job.UpdatedUtc, document.Id
                select new
                {
                    Document = document,
                    job.Status,
                    OutputConfirmedUtc = job.OutputType == "PDF_EXPORT" ? (DateTimeOffset?)null : job.OutputConfirmedUtc,
                    OutputCompletedUtc = completedUtc,
                    job.UpdatedUtc,
                    HasFinancialHold = hasFinancialHold
                })
                .Take(100)
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0) break;
            var hadStorageFailure = false;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DocumentRetentionRules.ShouldPurgeCloudDocument(
                    candidate.Document.StorageMode, candidate.Status,
                    candidate.OutputConfirmedUtc, candidate.OutputCompletedUtc,
                    candidate.UpdatedUtc, candidate.HasFinancialHold,
                    candidate.Document.PurgedUtc, completedCutoff, abandonedCutoff))
                    continue;

                try
                {
                    var photoSourceStorageKey = candidate.Document.PhotoSourceStorageKey;
                    if (!string.IsNullOrWhiteSpace(photoSourceStorageKey) &&
                        !string.Equals(photoSourceStorageKey, candidate.Document.StorageKey, StringComparison.Ordinal))
                    {
                        await storage.DeleteAsync(photoSourceStorageKey);
                    }
                    await storage.DeleteAsync(candidate.Document.StorageKey);
                    candidate.Document.PhotoSourceStorageKey = null;
                    candidate.Document.PhotoSourceOriginalFileName = null;
                    candidate.Document.PhotoSourceMediaType = null;
                    candidate.Document.PhotoSourceSha256 = null;
                    candidate.Document.PhotoSourceSizeBytes = null;
                    candidate.Document.PhotoEditorSettingsJson = null;
                    candidate.Document.PurgedUtc = now;
                    await db.SaveChangesAsync(cancellationToken);
                    purgedCount++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    db.Entry(candidate.Document).State = EntityState.Detached;
                    hadStorageFailure = true;
                    logger.LogWarning(exception, "Could not purge retained document {DocumentId}; it will be retried later.", candidate.Document.Id);
                }
            }

            if (hadStorageFailure) break;
        }

        // Local Agent files are deleted by the Agent's own seven-day queue
        // retention. Clear the server-side photo source hashes and editor JSON on
        // the same schedule so DOB/name overlays are not retained in job metadata.
        var expiredLocalPhotoMetadata = await (
            from document in db.PrintDocuments
            join job in db.PrintJobs
                on new { document.TenantId, PrintJobId = document.PrintJobId }
                equals new { job.TenantId, PrintJobId = job.Id }
            let completedUtc = job.OutputType == "PDF_EXPORT" ? job.OutputCompletedUtc : job.OutputConfirmedUtc
            let hasFinancialHold = db.PrintPayments.Any(payment =>
                payment.TenantId == job.TenantId && payment.PrintJobId == job.Id &&
                (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 payment.Status == "CUSTOMER_REPORTED" ||
                 payment.Status == "CASH_AWAITING_PAYMENT" ||
                 ((payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                  payment.RefundedAmountMinor < payment.AmountMinor)))
            where document.StorageMode == "LOCAL_AGENT" &&
                  document.PurgedUtc == null &&
                  (document.PhotoSourceSha256 != null || document.PhotoEditorSettingsJson != null) &&
                  ((job.Status == "COMPLETED" && completedUtc != null && completedUtc <= completedCutoff) ||
                   ((job.Status == "REJECTED" || job.Status == "PAYMENT_REJECTED" || job.Status == "QUOTE_EXPIRED" || job.Status == "REFUNDED") &&
                    job.OutputConfirmedUtc == null && job.OutputCompletedUtc == null &&
                    job.UpdatedUtc <= abandonedCutoff && !hasFinancialHold))
            orderby job.UpdatedUtc, document.Id
            select document)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var document in expiredLocalPhotoMetadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            document.PhotoSourceStorageKey = null;
            document.PhotoSourceOriginalFileName = null;
            document.PhotoSourceMediaType = null;
            document.PhotoSourceSha256 = null;
            document.PhotoSourceSizeBytes = null;
            document.PhotoEditorSettingsJson = null;
        }
        if (expiredLocalPhotoMetadata.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        var expiredTicketCutoff = now.AddDays(-settings.ExpiredUploadTicketRetentionDays);
        var removedTickets = await db.LocalPrintUploadTickets
            .Where(ticket => ticket.CompletedUtc == null && ticket.ExpiresUtc < expiredTicketCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var expiredSessionCutoff = now.AddDays(-settings.EmptyCustomerSessionRetentionDays);
        var removedSessions = await db.CustomerPrintSessions
            .Where(session => session.ExpiresUtc < expiredSessionCutoff &&
                !db.PrintJobs.Any(job => job.TenantId == session.TenantId && job.CustomerSessionId == session.Id) &&
                !db.LocalPrintUploadTickets.Any(ticket => ticket.TenantId == session.TenantId && ticket.CustomerSessionId == session.Id))
            .ExecuteDeleteAsync(cancellationToken);

        var staleTokenCutoff = now.AddDays(-settings.StaleRefreshTokenRetentionDays);
        var removedRefreshTokens = await db.RefreshTokens
            .Where(token => token.ExpiresUtc < staleTokenCutoff ||
                (token.RevokedUtc != null && token.RevokedUtc < staleTokenCutoff))
            .ExecuteDeleteAsync(cancellationToken);

        if (purgedCount + removedTickets + removedSessions + removedRefreshTokens > 0)
            logger.LogInformation(
                "Retention sweep complete. Cloud documents: {PurgedDocuments}; expired local tickets: {ExpiredTickets}; empty sessions: {ExpiredSessions}; stale refresh tokens: {StaleRefreshTokens}; completed retention: {CompletedRetentionDays}; abandoned retention: {AbandonedRetentionDays}.",
                purgedCount, removedTickets, removedSessions, removedRefreshTokens,
                settings.CloudCompletedDocumentRetentionDays, settings.AbandonedDocumentRetentionDays);

        return settings.ApiRetentionSweepHours;
    }
}
