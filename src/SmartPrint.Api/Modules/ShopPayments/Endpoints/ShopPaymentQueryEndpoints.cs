using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.ShopPayments.Endpoints;

/// <summary>
/// Read-only shop payment profile, payment ledger and refund history queries.
/// Provider verification, manual settlement, rejection and refund writes stay separate.
/// </summary>
public static class ShopPaymentQueryEndpoints
{
    public static RouteGroupBuilder MapShopPaymentQueries(this RouteGroupBuilder shop)
    {
        shop.MapGet("/payment-settings", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var settings = await db.ShopPaymentProfiles.AsNoTracking()
                .SingleOrDefaultAsync(item => item.TenantId == tenantId, ct);
            if (settings is null)
            {
                return Results.NotFound();
            }
        
            return Results.Ok(new
            {
                settings.UpiEnabled,
                settings.UpiId,
                settings.PayeeName,
                settings.CashEnabled,
                settings.InstantPaymentEnabled,
                settings.UpdatedUtc
            });
        });

        shop.MapGet("/payments", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId)) return Results.Forbid();
        
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
        
            var result = individualPayments.Select(item => (item.CreatedUtc, (object)item))
                .Concat(groupedBatchPayments.Select(item => (item.CreatedUtc, (object)item)))
                .OrderByDescending(item => item.CreatedUtc)
                .Take(100)
                .Select(item => item.Item2)
                .ToArray();
        
            return Results.Ok(result);
        });

        shop.MapGet("/refunds", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var refunds = await (
                from refund in db.PrintRefunds.AsNoTracking()
                join job in db.PrintJobs.AsNoTracking()
                    on new { refund.TenantId, PrintJobId = refund.PrintJobId } equals new { job.TenantId, PrintJobId = job.Id }
                join document in db.PrintDocuments.AsNoTracking()
                    on new { refund.TenantId, refund.PrintJobId } equals new { document.TenantId, document.PrintJobId }
                join user in db.Users.AsNoTracking()
                    on refund.RefundedByUserId equals user.Id
                where refund.TenantId == tenantId
                orderby refund.RefundedUtc descending
                select new
                {
                    refund.Id,
                    refund.PrintJobId,
                    refund.PrintPaymentId,
                    job.JobNumber,
                    DocumentName = document.OriginalFileName,
                    refund.AmountMinor,
                    refund.Currency,
                    refund.Method,
                    refund.Status,
                    refund.Reference,
                    refund.Reason,
                    refund.RefundedUtc,
                    refund.RefundedByUserId,
                    RefundedByName = user.DisplayName
                })
                .Take(100)
                .ToListAsync(ct);
        
            return Results.Ok(refunds);
        });
        return shop;
    }
}
