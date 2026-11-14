using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopPaymentReviewFailure
{
    None,
    NotFound,
    BatchNotReadyForVerification,
    PaymentNotReadyForVerification,
    BatchNotRejectable,
    PaymentNotRejectable,
    BatchAllocationsMismatch,
    BatchChangedConcurrently,
    PaymentChangedConcurrently
}

public sealed record ShopPaymentReviewOperationResult(
    ShopPaymentReviewFailure Failure,
    PrintBatchPayment? BatchPayment = null,
    PrintPayment? Payment = null,
    PrintJob? Job = null,
    int FileCount = 0,
    bool DuplicateRequest = false)
{
    public bool Succeeded => Failure == ShopPaymentReviewFailure.None;
}

public sealed class ShopPaymentReviewService(SmartPrintDbContext db)
{
    public async Task<ShopPaymentReviewOperationResult> VerifyAsync(
        Guid paymentId,
        Guid tenantId,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedBatchPayments = await db.PrintBatchPayments.FromSqlInterpolated(
            $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var batchPayment = lockedBatchPayments.SingleOrDefault();
        if (batchPayment is not null)
        {
            if (batchPayment.Status == "VERIFIED")
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ShopPaymentReviewOperationResult(
                    ShopPaymentReviewFailure.None, BatchPayment: batchPayment, DuplicateRequest: true);
            }

            if (!ShopPaymentReviewRules.CanVerify(batchPayment.Method, batchPayment.Status))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ShopPaymentReviewOperationResult(
                    ShopPaymentReviewFailure.BatchNotReadyForVerification, BatchPayment: batchPayment);
            }

            var batchFilePayments = await db.PrintPayments
                .Where(item => item.TenantId == tenantId && item.BatchPaymentId == batchPayment.Id)
                .ToListAsync(cancellationToken);
            var batchJobIds = batchFilePayments.Select(item => item.PrintJobId).ToArray();
            var batchJobs = await db.PrintJobs
                .Where(item => item.TenantId == tenantId && batchJobIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, batchFilePayments, batchJobs))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ShopPaymentReviewOperationResult(
                    ShopPaymentReviewFailure.BatchAllocationsMismatch, BatchPayment: batchPayment);
            }

            var now = DateTimeOffset.UtcNow;
            batchPayment.Status = "VERIFIED";
            batchPayment.VerifiedByUserId = actorId;
            batchPayment.VerifiedUtc = now;
            batchPayment.UpdatedUtc = now;
            var jobsById = batchJobs.ToDictionary(item => item.Id);
            foreach (var filePayment in batchFilePayments)
            {
                filePayment.Status = "VERIFIED";
                filePayment.VerifiedByUserId = actorId;
                filePayment.VerifiedUtc = now;
                filePayment.UpdatedUtc = now;
                var batchJob = jobsById[filePayment.PrintJobId];
                var batchPreviousStatus = batchJob.Status;
                var fileWasRejected = ShopPaymentReviewRules.MustPreserveRejectedJobStatus(batchJob.Status);
                if (!fileWasRejected)
                {
                    batchJob.Status = "PAID_AUTHORISED";
                    batchJob.UpdatedUtc = now;
                }

                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = tenantId,
                    PrintJobId = batchJob.Id,
                    PreviousStatus = batchPreviousStatus,
                    NewStatus = batchJob.Status,
                    ActorType = "ShopUser",
                    ActorId = actorId,
                    ReasonCode = fileWasRejected
                        ? "BATCH_PAYMENT_VERIFIED_FOR_REJECTED_FILE_REFUND"
                        : batchPayment.Method == "UPI"
                            ? "BATCH_UPI_PAYMENT_MANUALLY_VERIFIED"
                            : "BATCH_CASH_RECEIVED_AT_COUNTER",
                    CreatedUtc = now
                });
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ShopPaymentReviewOperationResult(
                    ShopPaymentReviewFailure.BatchChangedConcurrently);
            }

            await transaction.CommitAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(
                ShopPaymentReviewFailure.None,
                BatchPayment: batchPayment,
                FileCount: batchFilePayments.Count);
        }

        var payment = await db.PrintPayments.SingleOrDefaultAsync(
            item => item.Id == paymentId && item.TenantId == tenantId && item.BatchPaymentId == null,
            cancellationToken);
        if (payment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(ShopPaymentReviewFailure.NotFound);
        }

        if (payment.Status is "VERIFIED" or "PARTIALLY_REFUNDED")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(
                ShopPaymentReviewFailure.None, Payment: payment, DuplicateRequest: true);
        }

        if (!ShopPaymentReviewRules.CanVerify(payment.Method, payment.Status))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(
                ShopPaymentReviewFailure.PaymentNotReadyForVerification, Payment: payment);
        }

        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == payment.PrintJobId && item.TenantId == tenantId,
            cancellationToken);
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(ShopPaymentReviewFailure.NotFound);
        }

        var verifiedUtc = DateTimeOffset.UtcNow;
        payment.Status = "VERIFIED";
        payment.VerifiedByUserId = actorId;
        payment.VerifiedUtc = verifiedUtc;
        payment.UpdatedUtc = verifiedUtc;
        var previousJobStatus = job.Status;
        var singleFileWasRejected = ShopPaymentReviewRules.MustPreserveRejectedJobStatus(job.Status);
        if (!singleFileWasRejected)
        {
            job.Status = "PAID_AUTHORISED";
        }
        job.UpdatedUtc = verifiedUtc;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousJobStatus,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = actorId,
            ReasonCode = singleFileWasRejected
                ? "PAYMENT_VERIFIED_FOR_REJECTED_FILE_REFUND"
                : payment.Method == "UPI"
                    ? "UPI_PAYMENT_MANUALLY_VERIFIED"
                    : "CASH_RECEIVED_AT_COUNTER",
            CreatedUtc = verifiedUtc
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(ShopPaymentReviewFailure.PaymentChangedConcurrently);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ShopPaymentReviewOperationResult(
            ShopPaymentReviewFailure.None, Payment: payment, Job: job);
    }

    public async Task<ShopPaymentReviewOperationResult> RejectAsync(
        Guid paymentId,
        Guid tenantId,
        Guid? actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedBatchPayments = await db.PrintBatchPayments.FromSqlInterpolated(
            $"SELECT * FROM \"PrintBatchPayments\" WHERE \"Id\" = {paymentId} AND \"TenantId\" = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var batchPayment = lockedBatchPayments.SingleOrDefault();
        if (batchPayment is not null)
        {
            if (batchPayment.Status == "REJECTED")
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ShopPaymentReviewOperationResult(
                    ShopPaymentReviewFailure.None, BatchPayment: batchPayment, DuplicateRequest: true);
            }

            if (!ShopPaymentReviewRules.CanReject(batchPayment.Status))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ShopPaymentReviewOperationResult(
                    ShopPaymentReviewFailure.BatchNotRejectable, BatchPayment: batchPayment);
            }

            var batchFilePayments = await db.PrintPayments
                .Where(item => item.TenantId == tenantId && item.BatchPaymentId == batchPayment.Id)
                .ToListAsync(cancellationToken);
            var batchJobIds = batchFilePayments.Select(item => item.PrintJobId).ToArray();
            var batchJobs = await db.PrintJobs
                .Where(item => item.TenantId == tenantId && batchJobIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            if (!PrintBatchPaymentRules.HasConsistentAllocations(batchPayment, batchFilePayments, batchJobs))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ShopPaymentReviewOperationResult(
                    ShopPaymentReviewFailure.BatchAllocationsMismatch, BatchPayment: batchPayment);
            }

            var now = DateTimeOffset.UtcNow;
            batchPayment.Status = "REJECTED";
            batchPayment.RejectionReason = reason;
            batchPayment.UpdatedUtc = now;
            var jobsById = batchJobs.ToDictionary(item => item.Id);
            foreach (var filePayment in batchFilePayments)
            {
                filePayment.Status = "REJECTED";
                filePayment.RejectionReason = reason;
                filePayment.UpdatedUtc = now;
                var batchJob = jobsById[filePayment.PrintJobId];
                if (!ShopPaymentReviewRules.MustPreserveRejectedJobStatus(batchJob.Status))
                {
                    var batchPreviousStatus = batchJob.Status;
                    batchJob.Status = "PAYMENT_REJECTED";
                    batchJob.UpdatedUtc = now;
                    db.PrintJobEvents.Add(new PrintJobEvent
                    {
                        TenantId = tenantId,
                        PrintJobId = batchJob.Id,
                        PreviousStatus = batchPreviousStatus,
                        NewStatus = batchJob.Status,
                        ActorType = "ShopUser",
                        ActorId = actorId,
                        ReasonCode = "SHOP_REJECTED_BATCH_PAYMENT_REPORT",
                        CreatedUtc = now
                    });
                }
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ShopPaymentReviewOperationResult(
                    ShopPaymentReviewFailure.BatchChangedConcurrently);
            }

            await transaction.CommitAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(
                ShopPaymentReviewFailure.None,
                BatchPayment: batchPayment,
                FileCount: batchFilePayments.Count);
        }

        var payment = await db.PrintPayments.SingleOrDefaultAsync(
            item => item.Id == paymentId && item.TenantId == tenantId && item.BatchPaymentId == null,
            cancellationToken);
        if (payment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(ShopPaymentReviewFailure.NotFound);
        }

        if (payment.Status == "REJECTED")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(
                ShopPaymentReviewFailure.None, Payment: payment, DuplicateRequest: true);
        }

        if (!ShopPaymentReviewRules.CanReject(payment.Status))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(
                ShopPaymentReviewFailure.PaymentNotRejectable, Payment: payment);
        }

        var job = await db.PrintJobs.SingleOrDefaultAsync(
            item => item.Id == payment.PrintJobId && item.TenantId == tenantId,
            cancellationToken);
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(ShopPaymentReviewFailure.NotFound);
        }

        var rejectedUtc = DateTimeOffset.UtcNow;
        payment.Status = "REJECTED";
        payment.RejectionReason = reason;
        payment.UpdatedUtc = rejectedUtc;
        var previousStatus = job.Status;
        job.Status = "PAYMENT_REJECTED";
        job.UpdatedUtc = rejectedUtc;
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousStatus,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = actorId,
            ReasonCode = "SHOP_REJECTED_PAYMENT_REPORT",
            CreatedUtc = rejectedUtc
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPaymentReviewOperationResult(ShopPaymentReviewFailure.PaymentChangedConcurrently);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ShopPaymentReviewOperationResult(
            ShopPaymentReviewFailure.None, Payment: payment, Job: job);
    }
}
