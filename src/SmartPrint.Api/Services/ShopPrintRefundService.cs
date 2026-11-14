using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum ShopPrintRefundFailure
{
    None,
    NotFound,
    NotRefundableAfterPrintHandoff,
    VerifiedPaymentNotFound,
    InvalidPages,
    NoNewPagesRejected,
    RefundAmountBelowOneMinorUnit,
    IdempotencyKeyReused,
    ActorRequired,
    RecordedConcurrently
}

public sealed record ShopPrintRefundPreviewResult(
    ShopPrintRefundFailure Failure,
    Guid JobId = default,
    int PageCount = 0,
    int[]? RejectedPageNumbers = null,
    long OriginalAmountMinor = 0,
    long PreviouslyRefundedAmountMinor = 0,
    long AdditionalRefundAmountMinor = 0,
    long CumulativeRefundAmountMinor = 0,
    long RemainingPaidAmountMinor = 0,
    string? Currency = null,
    string? Message = null);

public sealed record ShopPrintRefundRecordResult(
    ShopPrintRefundFailure Failure,
    PrintRefund? Refund = null,
    PrintJob? Job = null,
    PrintPayment? Payment = null,
    int[]? RejectedPageNumbers = null,
    long CumulativeRefundAmountMinor = 0,
    bool DuplicateRequest = false,
    string? Message = null);

public sealed class ShopPrintRefundService(SmartPrintDbContext db)
{
    public async Task<ShopPrintRefundPreviewResult> PreviewAsync(
        Guid jobId,
        Guid tenantId,
        PrintRefundPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var job = await db.PrintJobs.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobId && item.TenantId == tenantId, cancellationToken);
        if (job is null)
        {
            return new ShopPrintRefundPreviewResult(ShopPrintRefundFailure.NotFound);
        }

        if (!PrintRefundRules.CanRefundBeforePrintHandoff(job))
        {
            return new ShopPrintRefundPreviewResult(ShopPrintRefundFailure.NotRefundableAfterPrintHandoff);
        }

        var payment = await db.PrintPayments.AsNoTracking().SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.PrintJobId == job.Id &&
                    (item.Status == "VERIFIED" || item.Status == "PARTIALLY_REFUNDED"),
            cancellationToken);
        if (!PrintRefundRules.HasRefundableVerifiedPayment(job, payment))
        {
            return new ShopPrintRefundPreviewResult(ShopPrintRefundFailure.VerifiedPaymentNotFound);
        }

        try
        {
            var previous = JsonSerializer.Deserialize<int[]>(job.RejectedPageNumbersJson) ?? [];
            var selected = PrintRefundRules.NormalizeRejectedPages(
                job.PageCount, request.RejectedPageNumbers, previous);
            var allocations = PrintRefundRules.NormalizePageAllocations(
                payment!.AmountMinor,
                job.PageCount,
                JsonSerializer.Deserialize<long[]>(job.PageRefundAllocationsJson));
            var additional = PrintRefundRules.CalculateAdditionalRefundAmount(
                payment.AmountMinor, payment.RefundedAmountMinor, selected, allocations);
            var cumulative = checked(payment.RefundedAmountMinor + additional);
            return new ShopPrintRefundPreviewResult(
                ShopPrintRefundFailure.None,
                JobId: job.Id,
                PageCount: job.PageCount,
                RejectedPageNumbers: selected,
                OriginalAmountMinor: payment.AmountMinor,
                PreviouslyRefundedAmountMinor: payment.RefundedAmountMinor,
                AdditionalRefundAmountMinor: additional,
                CumulativeRefundAmountMinor: cumulative,
                RemainingPaidAmountMinor: payment.AmountMinor - cumulative,
                Currency: payment.Currency);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException or JsonException)
        {
            return new ShopPrintRefundPreviewResult(
                ShopPrintRefundFailure.InvalidPages, Message: exception.Message);
        }
    }

    public async Task<ShopPrintRefundRecordResult> RecordAsync(
        Guid jobId,
        Guid tenantId,
        Guid? actorId,
        RecordPrintRefundRequest request,
        string reason,
        string method,
        string? reference,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var duplicate = await db.PrintRefunds.AsNoTracking().SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (duplicate is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (duplicate.PrintJobId != jobId)
            {
                return new ShopPrintRefundRecordResult(ShopPrintRefundFailure.IdempotencyKeyReused);
            }

            return new ShopPrintRefundRecordResult(
                ShopPrintRefundFailure.None, Refund: duplicate, DuplicateRequest: true);
        }

        // Serialize refund recording with the Agent's atomic claim UPDATE.
        // The first transaction to lock the row wins; the second reads the committed state.
        var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var job = lockedJobs.SingleOrDefault();
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintRefundRecordResult(ShopPrintRefundFailure.NotFound);
        }

        if (!PrintRefundRules.CanRefundBeforePrintHandoff(job))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintRefundRecordResult(ShopPrintRefundFailure.NotRefundableAfterPrintHandoff);
        }

        var payment = await db.PrintPayments.SingleOrDefaultAsync(
            item => item.TenantId == tenantId && item.PrintJobId == job.Id &&
                    (item.Status == "VERIFIED" || item.Status == "PARTIALLY_REFUNDED"),
            cancellationToken);
        if (!PrintRefundRules.HasRefundableVerifiedPayment(job, payment))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintRefundRecordResult(ShopPrintRefundFailure.VerifiedPaymentNotFound);
        }

        int[] rejected;
        long additional;
        long cumulative;
        try
        {
            var previous = JsonSerializer.Deserialize<int[]>(job.RejectedPageNumbersJson) ?? [];
            var selected = request.RejectedPageNumbers ?? Enumerable.Range(1, job.PageCount).ToArray();
            rejected = PrintRefundRules.NormalizeRejectedPages(job.PageCount, selected, previous);
            if (rejected.Length == previous.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ShopPrintRefundRecordResult(ShopPrintRefundFailure.NoNewPagesRejected);
            }

            var allocations = PrintRefundRules.NormalizePageAllocations(
                payment!.AmountMinor,
                job.PageCount,
                JsonSerializer.Deserialize<long[]>(job.PageRefundAllocationsJson));
            additional = PrintRefundRules.CalculateAdditionalRefundAmount(
                payment.AmountMinor, payment.RefundedAmountMinor, rejected, allocations);
            cumulative = checked(payment.RefundedAmountMinor + additional);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException or JsonException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintRefundRecordResult(
                ShopPrintRefundFailure.InvalidPages, Message: exception.Message);
        }

        if (additional <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintRefundRecordResult(ShopPrintRefundFailure.RefundAmountBelowOneMinorUnit);
        }

        if (actorId is not Guid actor)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintRefundRecordResult(ShopPrintRefundFailure.ActorRequired);
        }

        var now = DateTimeOffset.UtcNow;
        var previousJobStatus = job.Status;
        var fullyRefunded = cumulative >= payment!.AmountMinor || rejected.Length == job.PageCount;
        var refund = new PrintRefund
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PrintPaymentId = payment.Id,
            IdempotencyKey = idempotencyKey,
            AmountMinor = additional,
            Currency = payment.Currency,
            Method = method,
            Status = "RECORDED_AS_SENT",
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference,
            Reason = reason,
            RefundedByUserId = actor,
            RefundedUtc = now,
            CreatedUtc = now
        };
        job.RejectedPageNumbersJson = JsonSerializer.Serialize(rejected);
        job.Status = fullyRefunded ? "REFUNDED" : "PAID_AUTHORISED";
        job.FailureCode = fullyRefunded
            ? "SHOP_REJECTED_FILE_FULLY_REFUNDED"
            : "SHOP_REJECTED_PAGES_PARTIALLY_REFUNDED";
        job.UpdatedUtc = now;
        payment.RefundedAmountMinor = cumulative;
        payment.Status = fullyRefunded ? "REFUNDED" : "PARTIALLY_REFUNDED";
        payment.UpdatedUtc = now;
        db.PrintRefunds.Add(refund);
        db.PrintJobEvents.Add(new PrintJobEvent
        {
            TenantId = tenantId,
            PrintJobId = job.Id,
            PreviousStatus = previousJobStatus,
            NewStatus = job.Status,
            ActorType = "ShopUser",
            ActorId = actor,
            ReasonCode = fullyRefunded
                ? "SHOP_FILE_FULLY_REFUNDED"
                : "SHOP_PAGES_REJECTED_PARTIAL_REFUND_RECORDED",
            CreatedUtc = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintRefundRecordResult(ShopPrintRefundFailure.RecordedConcurrently);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            var raced = await db.PrintRefunds.AsNoTracking().SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.IdempotencyKey == idempotencyKey,
                cancellationToken);
            if (raced is not null)
            {
                if (raced.PrintJobId != jobId)
                {
                    return new ShopPrintRefundRecordResult(
                        ShopPrintRefundFailure.IdempotencyKeyReused);
                }

                return new ShopPrintRefundRecordResult(
                    ShopPrintRefundFailure.None, Refund: raced, DuplicateRequest: true);
            }

            throw;
        }

        return new ShopPrintRefundRecordResult(
            ShopPrintRefundFailure.None,
            Refund: refund,
            Job: job,
            Payment: payment,
            RejectedPageNumbers: rejected,
            CumulativeRefundAmountMinor: cumulative);
    }
}
