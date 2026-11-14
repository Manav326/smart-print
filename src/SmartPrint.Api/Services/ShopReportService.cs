using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Services;

public enum ShopReportFailure
{
    None,
    AccessInactive,
    AdvancedReportsNotIncluded,
    InvalidPeriod
}

public sealed record ShopReportResult(ShopReportFailure Failure, object? Summary = null);

public sealed class ShopReportService(
    SmartPrintDbContext db,
    TenantEntitlementService entitlements)
{
    public async Task<ShopReportResult> GetSummaryAsync(
        Guid tenantId,
        int periodDays,
        CancellationToken ct)
    {
        var plan = await entitlements.GetForTenantAsync(tenantId, ct);
        if (!plan.IsAccessActive)
        {
            return new(ShopReportFailure.AccessInactive);
        }
        if (!plan.AdvancedReportsEnabled)
        {
            return new(ShopReportFailure.AdvancedReportsNotIncluded);
        }
        if (!ShopReportRules.IsValidPeriod(periodDays))
        {
            return new(ShopReportFailure.InvalidPeriod);
        }

        var toUtc = DateTimeOffset.UtcNow;
        var fromUtc = toUtc.AddDays(-periodDays);
        var jobs = db.PrintJobs.AsNoTracking().Where(job =>
            job.TenantId == tenantId &&
            job.CreatedUtc >= fromUtc &&
            job.CreatedUtc < toUtc);

        var totalJobs = await jobs.CountAsync(ct);
        var completedJobs = await jobs.CountAsync(job =>
            job.Status == "COMPLETED" && job.OutputConfirmedUtc != null, ct);
        var outputUnconfirmedJobs = await jobs.CountAsync(job =>
            job.Status == "OUTPUT_UNCONFIRMED", ct);
        var pagesRequested = await jobs.SumAsync(job =>
            (long?)(job.PageCount * (long)job.Copies), ct) ?? 0;

        var jobsByStatus = await jobs
            .GroupBy(job => job.Status)
            .Select(group => new { status = group.Key, count = group.Count() })
            .OrderByDescending(group => group.count)
            .ThenBy(group => group.status)
            .ToListAsync(ct);

        var verifiedIndividualCollections = await db.PrintPayments.AsNoTracking()
            .Where(payment => payment.TenantId == tenantId &&
                              payment.BatchPaymentId == null &&
                              payment.VerifiedUtc != null &&
                              payment.VerifiedUtc >= fromUtc &&
                              payment.VerifiedUtc < toUtc)
            .GroupBy(payment => new { payment.Method, payment.Currency })
            .Select(group => new
            {
                method = group.Key.Method,
                currency = group.Key.Currency,
                paymentCount = group.Count(),
                amountMinor = group.Sum(payment => payment.AmountMinor)
            })
            .ToListAsync(ct);

        var verifiedBatchCollections = await db.PrintBatchPayments.AsNoTracking()
            .Where(payment => payment.TenantId == tenantId &&
                              payment.Status == "VERIFIED" &&
                              payment.VerifiedUtc != null &&
                              payment.VerifiedUtc >= fromUtc &&
                              payment.VerifiedUtc < toUtc)
            .GroupBy(payment => new { payment.Method, payment.Currency })
            .Select(group => new
            {
                method = group.Key.Method,
                currency = group.Key.Currency,
                paymentCount = group.Count(),
                amountMinor = group.Sum(payment => payment.AmountMinor)
            })
            .ToListAsync(ct);

        var verifiedCollectionsByMethod = verifiedIndividualCollections
            .Concat(verifiedBatchCollections)
            .GroupBy(item => new { item.method, item.currency })
            .Select(group => new
            {
                method = group.Key.method,
                currency = group.Key.currency,
                paymentCount = group.Sum(item => item.paymentCount),
                amountMinor = group.Sum(item => item.amountMinor)
            })
            .OrderBy(group => group.currency)
            .ThenBy(group => group.method)
            .ToList();

        var refundsRecordedByMethod = await db.PrintRefunds.AsNoTracking()
            .Where(refund => refund.TenantId == tenantId &&
                             refund.Status == "RECORDED_AS_SENT" &&
                             refund.RefundedUtc >= fromUtc &&
                             refund.RefundedUtc < toUtc)
            .GroupBy(refund => new { refund.Method, refund.Currency })
            .Select(group => new
            {
                method = group.Key.Method,
                currency = group.Key.Currency,
                refundCount = group.Count(),
                amountMinor = group.Sum(refund => refund.AmountMinor)
            })
            .OrderBy(group => group.currency)
            .ThenBy(group => group.method)
            .ToListAsync(ct);

        var pendingIndividualPaymentCount = await db.PrintPayments.AsNoTracking().CountAsync(payment =>
            payment.TenantId == tenantId &&
            payment.BatchPaymentId == null &&
            (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
             payment.Status == "CUSTOMER_REPORTED" ||
             payment.Status == "CASH_AWAITING_PAYMENT"), ct);
        var pendingBatchPaymentCount = await db.PrintBatchPayments.AsNoTracking().CountAsync(payment =>
            payment.TenantId == tenantId &&
            (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
             payment.Status == "CUSTOMER_REPORTED" ||
             payment.Status == "CASH_AWAITING_PAYMENT"), ct);
        var pendingPaymentCount = pendingIndividualPaymentCount + pendingBatchPaymentCount;

        return new ShopReportResult(ShopReportFailure.None, new
        {
            periodDays,
            fromUtc,
            toUtc,
            generatedUtc = DateTimeOffset.UtcNow,
            totalJobs,
            completedJobs,
            outputUnconfirmedJobs,
            pagesRequested,
            pendingPaymentCount,
            jobsByStatus,
            verifiedCollectionsByMethod,
            refundsRecordedByMethod
        });
    }
}