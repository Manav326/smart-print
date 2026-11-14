using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Pricing;

namespace SmartPrint.Api.Services;

public enum ShopPrintJobSettingsFailure
{
    None,
    NotFound,
    Locked,
    PaymentAlreadyStarted,
    PricingNotConfigured,
    DuplexPricingNotConfigured
}

public sealed record ShopPrintJobSettingsUpdateResult(
    ShopPrintJobSettingsFailure Failure,
    PrintJob? Job,
    string? Message = null)
{
    public bool Succeeded => Failure == ShopPrintJobSettingsFailure.None;
}

public sealed class ShopPrintJobSettingsService(SmartPrintDbContext db)
{
    public async Task<ShopPrintJobSettingsUpdateResult> ReviseAsync(
        Guid tenantId,
        Guid? actorId,
        Guid jobId,
        NormalizedPrintJobSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedJobs = await db.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"Id\" = {jobId} AND \"TenantId\" = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var job = lockedJobs.SingleOrDefault();
        if (job is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobSettingsUpdateResult(ShopPrintJobSettingsFailure.NotFound, null);
        }

        if (!PrintJobSettingsRules.CanRevise(job.PrintKind, job.Status))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobSettingsUpdateResult(ShopPrintJobSettingsFailure.Locked, null);
        }

        var hasStartedPayment = await db.PrintPayments.AsNoTracking().AnyAsync(payment =>
            payment.TenantId == tenantId && payment.PrintJobId == jobId && payment.Status != "REJECTED",
            cancellationToken);
        var batchPaymentStarted = job.BatchId is Guid batchId &&
            await db.PrintBatchPayments.AsNoTracking().AnyAsync(payment =>
                payment.TenantId == tenantId && payment.BatchId == batchId && payment.Status != "REJECTED",
                cancellationToken);
        if (hasStartedPayment || batchPaymentStarted)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobSettingsUpdateResult(ShopPrintJobSettingsFailure.PaymentAlreadyStarted, null);
        }

        var pricing = await db.ShopPricingProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.IsConfigured, cancellationToken);
        if (pricing is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobSettingsUpdateResult(ShopPrintJobSettingsFailure.PricingNotConfigured, null);
        }

        long amount;
        long[] pageRefundAllocations;
        try
        {
            amount = PricingCalculator.Calculate(
                job.PageCount, settings.Copies, settings.ColourMode, settings.IsDuplex, pricing);
            pageRefundAllocations = PricingCalculator.CalculatePageRefundAllocations(
                job.PageCount, settings.Copies, settings.ColourMode, settings.IsDuplex, pricing);
        }
        catch (DuplexPricingNotConfiguredException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ShopPrintJobSettingsUpdateResult(
                ShopPrintJobSettingsFailure.DuplexPricingNotConfigured,
                null,
                exception.Message);
        }

        var now = DateTimeOffset.UtcNow;
        var previousStatus = job.Status;
        job.ColourMode = settings.ColourMode;
        job.PaperSize = settings.PaperSize;
        job.IsDuplex = settings.IsDuplex;
        job.Copies = settings.Copies;
        job.Orientation = settings.Orientation;
        job.Currency = "INR";
        job.QuotedAmountMinor = amount;
        job.PageRefundAllocationsJson = JsonSerializer.Serialize(pageRefundAllocations);
        job.PricingVersion = pricing.Version;
        job.QuoteExpiresUtc = now.AddMinutes(10);
        job.Status = "AWAITING_CUSTOMER_APPROVAL";
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
            ReasonCode = "SHOP_REVISED_PRINT_SETTINGS_CUSTOMER_APPROVAL_REQUIRED",
            CreatedUtc = now
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ShopPrintJobSettingsUpdateResult(ShopPrintJobSettingsFailure.None, job);
    }
}
