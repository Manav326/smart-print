using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Services;

public sealed class ShopPaymentQueryService(SmartPrintDbContext db)
{
    public async Task<object[]> GetPaymentsAsync(Guid tenantId, CancellationToken ct)
    {
        var individualPayments = await (
            from payment in db.PrintPayments.AsNoTracking()
            join job in db.PrintJobs.AsNoTracking()
                on new { payment.TenantId, PrintJobId = payment.PrintJobId }
                equals new { job.TenantId, PrintJobId = job.Id }
            join document in db.PrintDocuments.AsNoTracking()
                on new { payment.TenantId, payment.PrintJobId }
                equals new { document.TenantId, document.PrintJobId }
            where payment.TenantId == tenantId && payment.BatchPaymentId == null
            orderby payment.CreatedUtc descending
            select new
            {
                payment.Id,
                payment.PrintJobId,
                job.JobNumber,
                DocumentName = document.OriginalFileName,
                job.Origin,
                job.RecipientName,
                job.RecipientPhone,
                BatchPaymentId = (Guid?)null,
                BatchFileCount = (int?)null,
                payment.Method,
                payment.Status,
                payment.AmountMinor,
                payment.RefundedAmountMinor,
                payment.Currency,
                payment.PaymentReference,
                payment.UpiIdSnapshot,
                payment.PayeeNameSnapshot,
                payment.RejectionReason,
                payment.CreatedUtc,
                payment.UpdatedUtc,
                payment.VerifiedUtc
            })
            .Take(100)
            .ToListAsync(ct);

        var recentBatchPayments = db.PrintBatchPayments.AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .OrderByDescending(item => item.CreatedUtc)
            .Take(100);

        var batchPaymentRows = await (
            from batchPayment in recentBatchPayments
            join payment in db.PrintPayments.AsNoTracking()
                on new { batchPayment.TenantId, BatchPaymentId = (Guid?)batchPayment.Id }
                equals new { payment.TenantId, payment.BatchPaymentId }
            join job in db.PrintJobs.AsNoTracking()
                on new { payment.TenantId, PrintJobId = payment.PrintJobId }
                equals new { job.TenantId, PrintJobId = job.Id }
            join document in db.PrintDocuments.AsNoTracking()
                on new { payment.TenantId, payment.PrintJobId }
                equals new { document.TenantId, document.PrintJobId }
            where batchPayment.TenantId == tenantId
            select new
            {
                batchPayment.Id,
                payment.PrintJobId,
                job.JobNumber,
                DocumentName = document.OriginalFileName,
                job.Origin,
                job.RecipientName,
                job.RecipientPhone,
                BatchPaymentId = (Guid?)batchPayment.Id,
                BatchFileCount = (int?)null,
                batchPayment.Method,
                batchPayment.Status,
                batchPayment.AmountMinor,
                payment.RefundedAmountMinor,
                batchPayment.Currency,
                batchPayment.PaymentReference,
                batchPayment.UpiIdSnapshot,
                batchPayment.PayeeNameSnapshot,
                batchPayment.RejectionReason,
                batchPayment.CreatedUtc,
                batchPayment.UpdatedUtc,
                batchPayment.VerifiedUtc
            })
            .ToListAsync(ct);

        var groupedBatchPayments = batchPaymentRows
            .GroupBy(item => item.Id)
            .Select(group =>
            {
                var first = group.First();
                var names = group.Select(item => item.DocumentName).Distinct().ToArray();
                var documentSummary = names.Length <= 3
                    ? string.Join(", ", names)
                    : string.Join(", ", names.Take(3)) + $" +{names.Length - 3} more";
                return new
                {
                    first.Id,
                    first.PrintJobId,
                    JobNumber = "Batch · " + first.JobNumber,
                    DocumentName = documentSummary,
                    first.Origin,
                    first.RecipientName,
                    first.RecipientPhone,
                    first.BatchPaymentId,
                    BatchFileCount = (int?)group.Count(),
                    first.Method,
                    first.Status,
                    first.AmountMinor,
                    RefundedAmountMinor = group.Sum(item => item.RefundedAmountMinor),
                    first.Currency,
                    first.PaymentReference,
                    first.UpiIdSnapshot,
                    first.PayeeNameSnapshot,
                    first.RejectionReason,
                    first.CreatedUtc,
                    first.UpdatedUtc,
                    first.VerifiedUtc
                };
            });

        return individualPayments.Select(item => (item.CreatedUtc, (object)item))
            .Concat(groupedBatchPayments.Select(item => (item.CreatedUtc, (object)item)))
            .OrderByDescending(item => item.CreatedUtc)
            .Take(100)
            .Select(item => item.Item2)
            .ToArray();
    }

    public async Task<List<object>> GetRefundsAsync(Guid tenantId, CancellationToken ct)
    {
        var refunds = await db.PrintRefunds.AsNoTracking()
            .Where(refund => refund.TenantId == tenantId)
            .Join(db.PrintJobs.AsNoTracking(),
                refund => new { refund.TenantId, PrintJobId = refund.PrintJobId },
                job => new { job.TenantId, PrintJobId = job.Id },
                (refund, job) => new { refund, job })
            .Join(db.PrintDocuments.AsNoTracking(),
                item => new { item.refund.TenantId, item.refund.PrintJobId },
                document => new { document.TenantId, document.PrintJobId },
                (item, document) => new { item.refund, item.job, document })
            .Join(db.Users.AsNoTracking(),
                item => item.refund.RefundedByUserId,
                user => user.Id,
                (item, user) => new
                {
                    item.refund.Id,
                    item.refund.PrintJobId,
                    item.refund.PrintPaymentId,
                    item.job.JobNumber,
                    DocumentName = item.document.OriginalFileName,
                    item.refund.AmountMinor,
                    item.refund.Currency,
                    item.refund.Method,
                    item.refund.Status,
                    item.refund.Reference,
                    item.refund.Reason,
                    item.refund.RefundedUtc,
                    item.refund.RefundedByUserId,
                    RefundedByName = user.DisplayName
                })
            .OrderByDescending(refund => refund.RefundedUtc)
            .Take(100)
            .ToListAsync(ct);
        return refunds.Select(refund => (object)refund).ToList();
    }
}