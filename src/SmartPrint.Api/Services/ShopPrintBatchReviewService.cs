using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopPrintBatchReviewFailure
{
    None,
    InvalidSelection,
    BatchCustomerSessionMismatch,
    BatchReviewChanged,
    BatchPaymentAlreadyStarted,
    FileHasPaymentHistory,
    FileNotReviewable,
    FileHasPaymentAttempt,
    FileRequiresRefundReview,
    IdDocumentBrandingNotAllowed,
    ChangedConcurrently
}

public sealed record ShopPrintBatchReviewResult(
    ShopPrintBatchReviewFailure Failure,
    Guid BatchId = default,
    int ReviewedCount = 0,
    int AcceptedCount = 0,
    int RejectedCount = 0,
    int QuoteRefreshRequiredCount = 0,
    DateTimeOffset? UpdatedUtc = null,
    Guid? JobId = null,
    string? JobNumber = null);

public sealed class ShopPrintBatchReviewService(SmartPrintDbContext db)
{
    public async Task<ShopPrintBatchReviewResult> ApplyAsync(
        Guid batchId,
        Guid tenantId,
        Guid? actorId,
        IEnumerable<Guid>? requestedReviewedJobIds,
        IEnumerable<Guid>? requestedAcceptedJobIds,
        bool pdfBrandingEnabled,
        CancellationToken cancellationToken)
    {
        if (!PrintBatchReviewRules.TryNormalize(
                requestedReviewedJobIds,
                requestedAcceptedJobIds,
                out var selection))
        {
            return new ShopPrintBatchReviewResult(ShopPrintBatchReviewFailure.InvalidSelection);
        }

        var reviewedIds = selection.ReviewedJobIds;
        var accepted = selection.AcceptedJobIds.ToHashSet();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var allBatchJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        if (allBatchJobs.Select(job => job.CustomerSessionId).Distinct().Count() > 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintBatchReviewResult(
                ShopPrintBatchReviewFailure.BatchCustomerSessionMismatch);
        }

        var lockedJobs = allBatchJobs
            .Where(job => job.Status is "AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED" or "REJECTED")
            .ToList();
        var lockedJobIds = lockedJobs.Select(job => job.Id).ToHashSet();
        if (lockedJobs.Count != reviewedIds.Length || reviewedIds.Any(id => !lockedJobIds.Contains(id)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintBatchReviewResult(ShopPrintBatchReviewFailure.BatchReviewChanged);
        }

        var batchPaymentStarted = await db.PrintBatchPayments.AsNoTracking().AnyAsync(item =>
            item.TenantId == tenantId && item.BatchId == batchId && item.Status != "REJECTED",
            cancellationToken);
        if (batchPaymentStarted)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintBatchReviewResult(ShopPrintBatchReviewFailure.BatchPaymentAlreadyStarted);
        }

        var paymentProtectedJobIds = await db.PrintPayments.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           reviewedIds.Contains(item.PrintJobId) &&
                           (item.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                            item.Status == "CUSTOMER_REPORTED" ||
                            item.Status == "CASH_AWAITING_PAYMENT" ||
                            item.Status == "VERIFIED" ||
                            item.Status == "PARTIALLY_REFUNDED"))
            .Select(item => item.PrintJobId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var protectedJobIds = paymentProtectedJobIds.ToHashSet();

        if (!PrintBatchReviewRules.CanEnablePdfBranding(
                pdfBrandingEnabled, lockedJobs.Select(job => job.PrintKind)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintBatchReviewResult(
                ShopPrintBatchReviewFailure.IdDocumentBrandingNotAllowed);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var job in lockedJobs)
        {
            if (job.PdfBrandingEnabled == pdfBrandingEnabled)
                continue;

            job.PdfBrandingEnabled = pdfBrandingEnabled;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = job.Id,
                PreviousStatus = job.Status,
                NewStatus = job.Status,
                ActorType = "ShopUser",
                ActorId = actorId,
                ReasonCode = pdfBrandingEnabled
                    ? "SHOP_BATCH_PDF_BRANDING_ENABLED"
                    : "SHOP_BATCH_PDF_BRANDING_DISABLED",
                CreatedUtc = now
            });
        }

        var acceptedCount = 0;
        var rejectedCount = 0;
        var quoteRefreshRequiredCount = 0;
        foreach (var job in lockedJobs)
        {
            var shouldAccept = accepted.Contains(job.Id);
            if (shouldAccept)
            {
                acceptedCount++;
                if (job.Status == "REJECTED")
                {
                    if (protectedJobIds.Contains(job.Id))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new ShopPrintBatchReviewResult(
                            ShopPrintBatchReviewFailure.FileHasPaymentHistory,
                            JobId: job.Id,
                            JobNumber: job.JobNumber);
                    }

                    var previousStatus = job.Status;
                    var quoteExpired = PrintBatchCheckoutRules.IsQuoteExpired(job.QuoteExpiresUtc, now);
                    job.Status = quoteExpired ? "QUOTE_EXPIRED" : "AWAITING_PAYMENT";
                    if (quoteExpired) quoteRefreshRequiredCount++;
                    job.FailureCode = null;
                    job.UpdatedUtc = now;
                    db.PrintJobEvents.Add(new PrintJobEvent
                    {
                        TenantId = tenantId,
                        PrintJobId = job.Id,
                        PreviousStatus = previousStatus,
                        NewStatus = job.Status,
                        ActorType = "ShopUser",
                        ActorId = actorId,
                        ReasonCode = quoteExpired
                            ? "SHOP_RESTORED_REJECTED_BATCH_FILE_REQUOTE_REQUIRED"
                            : "SHOP_RESTORED_REJECTED_BATCH_FILE",
                        CreatedUtc = now
                    });
                }
                else if (job.Status == "AWAITING_SHOP_APPROVAL")
                {
                    var previousStatus = job.Status;
                    var quoteExpired = PrintBatchCheckoutRules.IsQuoteExpired(job.QuoteExpiresUtc, now);
                    job.Status = quoteExpired ? "QUOTE_EXPIRED" : "AWAITING_PAYMENT";
                    if (quoteExpired) quoteRefreshRequiredCount++;
                    job.FailureCode = null;
                    job.UpdatedUtc = now;
                    db.PrintJobEvents.Add(new PrintJobEvent
                    {
                        TenantId = tenantId,
                        PrintJobId = job.Id,
                        PreviousStatus = previousStatus,
                        NewStatus = job.Status,
                        ActorType = "ShopUser",
                        ActorId = actorId,
                        ReasonCode = quoteExpired
                            ? "SHOP_REVIEW_FOUND_EXPIRED_BATCH_QUOTE"
                            : "SHOP_APPROVED_BATCH_AWAITING_PAYMENT",
                        CreatedUtc = now
                    });
                }
                else if (job.Status is "AWAITING_PAYMENT" or "PAYMENT_REJECTED")
                {
                    if (PrintBatchCheckoutRules.IsQuoteExpired(job.QuoteExpiresUtc, now))
                    {
                        var previousStatus = job.Status;
                        job.Status = "QUOTE_EXPIRED";
                        job.FailureCode = null;
                        job.UpdatedUtc = now;
                        quoteRefreshRequiredCount++;
                        db.PrintJobEvents.Add(new PrintJobEvent
                        {
                            TenantId = tenantId,
                            PrintJobId = job.Id,
                            PreviousStatus = previousStatus,
                            NewStatus = job.Status,
                            ActorType = "ShopUser",
                            ActorId = actorId,
                            ReasonCode = "SHOP_REVIEW_FOUND_EXPIRED_BATCH_QUOTE",
                            CreatedUtc = now
                        });
                    }
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new ShopPrintBatchReviewResult(
                        ShopPrintBatchReviewFailure.FileNotReviewable,
                        JobId: job.Id,
                        JobNumber: job.JobNumber);
                }
            }
            else
            {
                rejectedCount++;
                if (job.Status == "REJECTED")
                {
                    continue;
                }

                if (protectedJobIds.Contains(job.Id))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new ShopPrintBatchReviewResult(
                        ShopPrintBatchReviewFailure.FileHasPaymentAttempt,
                        JobId: job.Id,
                        JobNumber: job.JobNumber);
                }

                if (job.Status is not ("AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT" or "PAYMENT_REJECTED"))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new ShopPrintBatchReviewResult(
                        ShopPrintBatchReviewFailure.FileRequiresRefundReview,
                        JobId: job.Id,
                        JobNumber: job.JobNumber);
                }

                var previousStatus = job.Status;
                job.Status = "REJECTED";
                job.FailureCode = "SHOP_REJECTED";
                job.UpdatedUtc = now;
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = tenantId,
                    PrintJobId = job.Id,
                    PreviousStatus = previousStatus,
                    NewStatus = job.Status,
                    ActorType = "ShopUser",
                    ActorId = actorId,
                    ReasonCode = "SHOP_REJECTED_BATCH_FILE",
                    CreatedUtc = now
                });
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintBatchReviewResult(ShopPrintBatchReviewFailure.ChangedConcurrently);
        }

        return new ShopPrintBatchReviewResult(
            ShopPrintBatchReviewFailure.None,
            BatchId: batchId,
            ReviewedCount: lockedJobs.Count,
            AcceptedCount: acceptedCount,
            RejectedCount: rejectedCount,
            QuoteRefreshRequiredCount: quoteRefreshRequiredCount,
            UpdatedUtc: now);
    }
}
