using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Endpoints;

public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this WebApplication app)
    {
        var customerApi = app.MapGroup("/api/v1/customer").RequireAuthorization();
        
        customerApi.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<AppUser> userManager) =>
        {
            var customerId = CustomerIdentity.GetCustomerId(principal);
            if (customerId is null)
            {
                return Results.Forbid();
            }
        
            var user = await userManager.FindByIdAsync(customerId.Value.ToString());
            if (user is null || user.TenantId is not null)
            {
                return Results.Unauthorized();
            }
        
            return Results.Ok(new
            {
                user.Id,
                user.DisplayName,
                user.Email
            });
        });
        
        customerApi.MapGet("/jobs/count", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var customerId = CustomerIdentity.GetCustomerId(principal);
            if (customerId is null)
            {
                return Results.Forbid();
            }
        
            var totalCount = await (
                from job in db.PrintJobs.AsNoTracking()
                join profile in db.ShopProfiles.AsNoTracking()
                    on job.TenantId equals profile.TenantId
                where job.CustomerUserId == customerId ||
                      (job.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                          session.TenantId == job.TenantId &&
                          session.Id == job.CustomerSessionId &&
                          session.CustomerUserId == customerId))
                select job.Id
            ).CountAsync(ct);
        
            return Results.Ok(new { TotalCount = totalCount });
        });
        
        customerApi.MapGet("/jobs", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct,
            int? page,
            int? size) =>
        {
            var customerId = CustomerIdentity.GetCustomerId(principal);
            if (customerId is null)
            {
                return Results.Forbid();
            }
        
            // Match the zero-based page/size contract used by mPay. Reject invalid
            // values rather than silently changing the page the customer requested.
            var pageNumber = page ?? 0;
            var pageSize = size ?? 10;
            if (pageNumber < 0 || pageSize is < 1 or > 100)
            {
                return Results.BadRequest(new { message = "Page must be zero or greater and size must be between 1 and 100." });
            }
        
            var skipLong = (long)pageNumber * pageSize;
            if (skipLong > int.MaxValue)
            {
                return Results.BadRequest(new { message = "The requested history page is outside the supported range." });
            }
        
            var customerJobs = (
                from job in db.PrintJobs.AsNoTracking()
                join profile in db.ShopProfiles.AsNoTracking()
                    on job.TenantId equals profile.TenantId
                where job.CustomerUserId == customerId ||
                      (job.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                          session.TenantId == job.TenantId &&
                          session.Id == job.CustomerSessionId &&
                          session.CustomerUserId == customerId))
                orderby job.CreatedUtc descending, job.Id descending
                select new
                {
                    job.Id,
                    job.PrivatePrinting,
                    job.UpdatedUtc,
                    job.BatchId,
                    job.BatchGroupName,
                    job.BatchName,
                    job.JobNumber,
                    job.Status,
                    job.PageCount,
                    job.Copies,
                    job.Currency,
                    job.QuotedAmountMinor,
                    job.CreatedUtc,
                    job.RejectedPageNumbersJson,
                    job.OutputCompletedUtc,
                    ShopName = profile.DisplayName,
                    OriginalFileName = job.PrivatePrinting
                        ? "Private document"
                        : db.PrintDocuments.AsNoTracking()
                            .Where(document => document.TenantId == job.TenantId && document.PrintJobId == job.Id)
                            .Select(document => document.OriginalFileName)
                            .FirstOrDefault(),
                    PaidAmountMinor = db.PrintPayments.AsNoTracking()
                        .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id &&
                            (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED" || payment.Status == "REFUNDED"))
                        .Sum(payment => (long?)payment.AmountMinor) ?? 0L,
                    RefundedAmountMinor = db.PrintPayments.AsNoTracking()
                        .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id)
                        .Sum(payment => (long?)payment.RefundedAmountMinor) ?? 0L,
                    PaymentStatus = db.PrintPayments.AsNoTracking()
                        .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id)
                        .OrderByDescending(payment => payment.UpdatedUtc)
                        .Select(payment => payment.Status)
                        .FirstOrDefault(),
                    PaymentMethod = db.PrintPayments.AsNoTracking()
                        .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id)
                        .OrderByDescending(payment => payment.UpdatedUtc)
                        .Select(payment => payment.Method)
                        .FirstOrDefault()
                });
        
            var totalItems = await customerJobs.CountAsync(ct);
            var totalPages = totalItems == 0
                ? 0
                : (int)Math.Ceiling(totalItems / (double)pageSize);
            var jobs = await customerJobs
                .Skip((int)skipLong)
                .Take(pageSize)
                .ToListAsync(ct);
        
            return Results.Ok(new
            {
                Items = jobs,
                Page = pageNumber,
                Size = pageSize,
                TotalItems = totalItems,
                TotalPages = totalPages,
                HasNext = (long)pageNumber + 1 < totalPages
            });
        });
        
        
        customerApi.MapGet("/jobs/{jobId:guid}/status", async (
            Guid jobId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var customerId = CustomerIdentity.GetCustomerId(principal);
            if (customerId is null)
            {
                return Results.Forbid();
            }
        
            // A signed-in customer owns the order through the customer identity attached
            // to the originating session. Do not require that the short-lived QR session
            // remains active: history tracking must survive app restarts and session expiry.
            var ownedJob = await db.PrintJobs.AsNoTracking()
                .Where(trackedPrintJob => trackedPrintJob.Id == jobId &&
                    (trackedPrintJob.CustomerUserId == customerId ||
                     (trackedPrintJob.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                         session.TenantId == trackedPrintJob.TenantId &&
                         session.Id == trackedPrintJob.CustomerSessionId &&
                         session.CustomerUserId == customerId))))
                .Select(trackedPrintJob => new { trackedPrintJob.TenantId })
                .SingleOrDefaultAsync(ct);
        
            if (ownedJob is null)
            {
                return Results.NotFound();
            }
        
            var jobStatus = await db.PrintJobs.AsNoTracking()
                .Where(item => item.Id == jobId && item.TenantId == ownedJob.TenantId &&
                    (item.CustomerUserId == customerId ||
                     (item.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                         session.TenantId == item.TenantId &&
                         session.Id == item.CustomerSessionId &&
                         session.CustomerUserId == customerId))))
                .Select(item => new
                {
                    item.Id,
                    item.BatchGroupName,
                    item.BatchName,
                    item.JobNumber,
                    item.Status,
                    item.PageCount,
                    item.Copies,
                    item.ColourMode,
                    item.PaperSize,
                    item.IsDuplex,
                    item.Orientation,
                    item.PrintKind,
                    item.PhotoSizeCode,
                    item.PhotoCount,
                    item.PhotosPerPage,
                    item.Currency,
                    item.QuotedAmountMinor,
                    item.RejectedPageNumbersJson,
                    item.OutputCompletedUtc,
                    item.UpdatedUtc,
                    item.PrivatePrinting,
                    item.PrivateDocumentPurgedUtc,
                    OriginalFileName = item.PrivatePrinting
                        ? "Private document"
                        : db.PrintDocuments.AsNoTracking()
                            .Where(document => document.TenantId == item.TenantId && document.PrintJobId == item.Id)
                            .Select(document => document.OriginalFileName)
                            .FirstOrDefault()
                })
                .SingleAsync(ct);
        
            var timeline = await db.PrintJobEvents.AsNoTracking()
                .Where(item => item.TenantId == ownedJob.TenantId && item.PrintJobId == jobId)
                .OrderBy(item => item.CreatedUtc)
                .Select(item => new { item.NewStatus, item.ActorType, item.CreatedUtc })
                .ToListAsync(ct);
        
            var payment = await db.PrintPayments.AsNoTracking()
                .Where(item => item.TenantId == ownedJob.TenantId && item.PrintJobId == jobId)
                .OrderByDescending(item => item.CreatedUtc)
                .Select(item => new
                {
                    item.Id,
                    item.BatchPaymentId,
                    item.Method,
                    item.Status,
                    item.AmountMinor,
                    item.RefundedAmountMinor,
                    item.Currency,
                    item.PaymentReference,
                    item.RejectionReason,
                    item.CreatedUtc,
                    item.UpdatedUtc,
                    item.VerifiedUtc
                })
                .FirstOrDefaultAsync(ct);
        
            var batchPayment = payment?.BatchPaymentId is Guid batchPaymentId
                ? await db.PrintBatchPayments.AsNoTracking()
                    .Where(item => item.Id == batchPaymentId && item.TenantId == ownedJob.TenantId)
                    .Select(item => new
                    {
                        item.Id,
                        item.Method,
                        item.Status,
                        item.AmountMinor,
                        item.Currency,
                        item.BatchId,
                        item.PaymentReference,
                        item.RejectionReason,
                        item.CreatedUtc,
                        item.UpdatedUtc,
                        item.VerifiedUtc
                    })
                    .SingleOrDefaultAsync(ct)
                : null;
        
            return Results.Ok(new { Job = jobStatus, Timeline = timeline, Payment = payment, BatchPayment = batchPayment });
        });
        
        
        customerApi.MapGet("/orders/{orderKey:guid}/tracking", async (
            Guid orderKey,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var customerId = CustomerIdentity.GetCustomerId(principal);
            if (customerId is null)
            {
                return Results.Forbid();
            }
        
            // Resolve the requested batch (or a standalone job) through customer ownership
            // first. The batch identifier is never sufficient authorization on its own.
            var anchorJob = await db.PrintJobs.AsNoTracking()
                .Where(item =>
                    (item.BatchId == orderKey || (item.Id == orderKey && item.BatchId == null)) &&
                    (item.CustomerUserId == customerId ||
                     (item.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                         session.TenantId == item.TenantId &&
                         session.Id == item.CustomerSessionId &&
                         session.CustomerUserId == customerId))))
                .OrderBy(item => item.CreatedUtc)
                .Select(item => new { item.Id, item.TenantId, item.BatchId })
                .FirstOrDefaultAsync(ct);
        
            if (anchorJob is null)
            {
                return Results.NotFound();
            }
        
            var jobs = await db.PrintJobs.AsNoTracking()
                .Where(item =>
                    item.TenantId == anchorJob.TenantId &&
                    (anchorJob.BatchId != null ? item.BatchId == anchorJob.BatchId : item.Id == anchorJob.Id) &&
                    (item.CustomerUserId == customerId ||
                     (item.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                         session.TenantId == item.TenantId &&
                         session.Id == item.CustomerSessionId &&
                         session.CustomerUserId == customerId))))
                .OrderBy(item => item.CreatedUtc)
                .Select(item => new
                {
                    item.Id,
                    item.BatchId,
                    item.BatchGroupName,
                    item.BatchName,
                    item.JobNumber,
                    item.Status,
                    item.PageCount,
                    item.Copies,
                    item.ColourMode,
                    item.PaperSize,
                    item.IsDuplex,
                    item.Orientation,
                    item.PrintKind,
                    item.PhotoSizeCode,
                    item.PhotoCount,
                    item.PhotosPerPage,
                    item.Currency,
                    item.QuotedAmountMinor,
                    item.RejectedPageNumbersJson,
                    item.OutputType,
                    item.OutputCompletedUtc,
                    item.UpdatedUtc,
                    item.CreatedUtc,
                    item.PrivatePrinting,
                    ShopName = db.ShopProfiles.AsNoTracking()
                        .Where(profile => profile.TenantId == item.TenantId)
                        .Select(profile => profile.DisplayName)
                        .FirstOrDefault() ?? "Print shop",
                    OriginalFileName = item.PrivatePrinting
                        ? "Private document"
                        : db.PrintDocuments.AsNoTracking()
                            .Where(document => document.TenantId == item.TenantId && document.PrintJobId == item.Id)
                            .Select(document => document.OriginalFileName)
                            .FirstOrDefault() ?? "Print document"
                })
                .ToListAsync(ct);
        
            if (jobs.Count == 0)
            {
                return Results.NotFound();
            }
        
            var jobIds = jobs.Select(item => item.Id).ToArray();
            var events = await db.PrintJobEvents.AsNoTracking()
                .Where(item => item.TenantId == anchorJob.TenantId && jobIds.Contains(item.PrintJobId))
                .OrderBy(item => item.CreatedUtc)
                .Select(item => new
                {
                    item.PrintJobId,
                    item.PreviousStatus,
                    item.NewStatus,
                    item.ActorType,
                    item.CreatedUtc
                })
                .ToListAsync(ct);
        
            var payments = await db.PrintPayments.AsNoTracking()
                .Where(item => item.TenantId == anchorJob.TenantId && jobIds.Contains(item.PrintJobId))
                .OrderByDescending(item => item.UpdatedUtc)
                .Select(item => new
                {
                    item.Id,
                    item.PrintJobId,
                    item.BatchPaymentId,
                    item.Method,
                    item.Status,
                    item.AmountMinor,
                    item.RefundedAmountMinor,
                    item.Currency,
                    item.RejectionReason,
                    item.CreatedUtc,
                    item.UpdatedUtc,
                    item.VerifiedUtc
                })
                .ToListAsync(ct);
        
            var batchPaymentIds = payments
                .Where(item => item.BatchPaymentId != null)
                .Select(item => item.BatchPaymentId!.Value)
                .Distinct()
                .ToArray();
            var batchPayments = await db.PrintBatchPayments.AsNoTracking()
                .Where(item => item.TenantId == anchorJob.TenantId && batchPaymentIds.Contains(item.Id))
                .OrderByDescending(item => item.UpdatedUtc)
                .Select(item => new
                {
                    item.Id,
                    item.BatchId,
                    item.Method,
                    item.Status,
                    item.AmountMinor,
                    item.Currency,
                    item.RejectionReason,
                    item.CreatedUtc,
                    item.UpdatedUtc,
                    item.VerifiedUtc
                })
                .ToListAsync(ct);
        
            var first = jobs[0];
            return Results.Ok(new
            {
                Order = new
                {
                    Key = anchorJob.BatchId ?? anchorJob.Id,
                    BatchId = anchorJob.BatchId,
                    first.BatchName,
                    first.BatchGroupName,
                    first.ShopName,
                    CreatedUtc = jobs.Min(item => item.CreatedUtc),
                    UpdatedUtc = jobs.Max(item => item.UpdatedUtc),
                    FileCount = jobs.Count
                },
                Files = jobs.Select(job => new
                {
                    Job = job,
                    Timeline = events.Where(item => item.PrintJobId == job.Id).ToArray(),
                    Payment = payments.FirstOrDefault(item => item.PrintJobId == job.Id)
                }).ToArray(),
                BatchPayment = batchPayments.FirstOrDefault()
            });
        });
        
    }
}
