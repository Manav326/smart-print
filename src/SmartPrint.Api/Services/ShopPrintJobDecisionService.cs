using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopPrintJobDecisionFailure
{
    None,
    NotFound,
    NotReadyForApproval,
    QuoteExpired,
    NotReadyForRejection
}

public sealed record ShopPrintJobDecisionResult(
    ShopPrintJobDecisionFailure Failure,
    Guid? JobId = null,
    string? JobNumber = null,
    string? Status = null,
    string? Currency = null,
    long? QuotedAmountMinor = null,
    DateTimeOffset? UpdatedUtc = null,
    bool DuplicateRequest = false,
    bool HasConfiguredPaymentMethod = false)
{
    public bool Succeeded => Failure == ShopPrintJobDecisionFailure.None;
}

public sealed class ShopPrintJobDecisionService(SmartPrintDbContext db)
{
    public async Task<ShopPrintJobDecisionResult> ApproveAsync(
        Guid tenantId,
        Guid? actorId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var job = lockedJobs.SingleOrDefault();
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobDecisionResult(ShopPrintJobDecisionFailure.NotFound);
        }

        if (job.Status == "AWAITING_PAYMENT")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobDecisionResult(
                ShopPrintJobDecisionFailure.None,
                job.Id,
                job.JobNumber,
                job.Status,
                UpdatedUtc: job.UpdatedUtc,
                DuplicateRequest: true);
        }

        if (job.Status != "AWAITING_SHOP_APPROVAL")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobDecisionResult(ShopPrintJobDecisionFailure.NotReadyForApproval);
        }

        var now = DateTimeOffset.UtcNow;
        if (job.QuoteExpiresUtc is null || job.QuoteExpiresUtc <= now)
        {
            var expiredFromStatus = job.Status;
            job.Status = "QUOTE_EXPIRED";
            job.UpdatedUtc = now;
            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = job.TenantId,
                PrintJobId = job.Id,
                PreviousStatus = expiredFromStatus,
                NewStatus = job.Status,
                ActorType = "ShopUser",
                ActorId = actorId,
                ReasonCode = "QUOTE_EXPIRED_BEFORE_SHOP_APPROVAL",
                CreatedUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ShopPrintJobDecisionResult(ShopPrintJobDecisionFailure.QuoteExpired);
        }

        var previousStatus = job.Status;
        job.Status = "AWAITING_PAYMENT";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousStatus,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = actorId,
            ReasonCode = "SHOP_APPROVED_AWAITING_PAYMENT",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var paymentSettings = await db.ShopPaymentProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId, cancellationToken);
        var hasConfiguredPaymentMethod = paymentSettings?.CashEnabled == true ||
            (paymentSettings?.UpiEnabled == true &&
             !string.IsNullOrWhiteSpace(paymentSettings.UpiId) &&
             !string.IsNullOrWhiteSpace(paymentSettings.PayeeName));

        return new ShopPrintJobDecisionResult(
            ShopPrintJobDecisionFailure.None,
            job.Id,
            job.JobNumber,
            job.Status,
            job.Currency,
            job.QuotedAmountMinor,
            job.UpdatedUtc,
            HasConfiguredPaymentMethod: hasConfiguredPaymentMethod);
    }

    public async Task<ShopPrintJobDecisionResult> RejectAsync(
        Guid tenantId,
        Guid? actorId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var job = lockedJobs.SingleOrDefault();
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobDecisionResult(ShopPrintJobDecisionFailure.NotFound);
        }

        if (job.Status == "REJECTED")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobDecisionResult(
                ShopPrintJobDecisionFailure.None,
                job.Id,
                job.JobNumber,
                job.Status,
                UpdatedUtc: job.UpdatedUtc,
                DuplicateRequest: true);
        }

        if (job.Status is not ("AWAITING_SHOP_APPROVAL" or "AWAITING_PAYMENT"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobDecisionResult(ShopPrintJobDecisionFailure.NotReadyForRejection);
        }

        var previousStatus = job.Status;
        var now = DateTimeOffset.UtcNow;
        job.Status = "REJECTED";
        job.FailureCode = "SHOP_REJECTED";
        job.UpdatedUtc = now;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = job.TenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousStatus,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = actorId,
            ReasonCode = "SHOP_REJECTED",
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ShopPrintJobDecisionResult(
            ShopPrintJobDecisionFailure.None,
            job.Id,
            job.JobNumber,
            job.Status,
            UpdatedUtc: job.UpdatedUtc);
    }
}
