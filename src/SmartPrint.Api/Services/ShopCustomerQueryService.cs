using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public sealed class ShopCustomerQueryService(SmartPrintDbContext db)
{
    public async Task<object> ListAsync(
        Guid tenantId,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var customerSessions =
            from session in db.CustomerPrintSessions.AsNoTracking()
            join user in db.Users.AsNoTracking()
                on session.CustomerUserId equals (Guid?)user.Id
            where session.TenantId == tenantId && user.TenantId == null
            select new
            {
                CustomerId = user.Id,
                user.DisplayName,
                user.Email,
                session.CreatedUtc
            };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            customerSessions = customerSessions.Where(item =>
                EF.Functions.ILike(item.DisplayName, pattern) ||
                (item.Email != null && EF.Functions.ILike(item.Email, pattern)));
        }

        var groupedCustomers = customerSessions
            .GroupBy(item => new { item.CustomerId, item.DisplayName, item.Email })
            .Select(group => new
            {
                group.Key.CustomerId,
                group.Key.DisplayName,
                group.Key.Email,
                FirstSeenUtc = group.Min(item => item.CreatedUtc),
                LastSeenUtc = group.Max(item => item.CreatedUtc),
                SessionCount = group.Count()
            });

        var totalCount = await groupedCustomers.CountAsync(ct);
        var customers = await groupedCustomers
            .OrderByDescending(item => item.LastSeenUtc)
            .ThenBy(item => item.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var customerIds = customers.Select(item => item.CustomerId).ToArray();
        var jobStatistics = await (
            from job in db.PrintJobs.AsNoTracking()
            where job.TenantId == tenantId &&
                  ((job.CustomerUserId != null && customerIds.Contains(job.CustomerUserId.Value)) ||
                   (job.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                       session.TenantId == tenantId &&
                       session.Id == job.CustomerSessionId &&
                       session.CustomerUserId != null &&
                       customerIds.Contains(session.CustomerUserId.Value))))
            select new
            {
                CustomerId = job.CustomerUserId ?? db.CustomerPrintSessions
                    .Where(session => session.TenantId == tenantId && session.Id == job.CustomerSessionId)
                    .Select(session => session.CustomerUserId)
                    .FirstOrDefault(),
                job.Status,
                job.OutputType,
                job.OutputCompletedUtc,
                job.OutputConfirmedUtc,
                job.CreatedUtc
            } into customerJob
            where customerJob.CustomerId != null
            group customerJob by customerJob.CustomerId!.Value into customerJobs
            select new
            {
                CustomerId = customerJobs.Key,
                PrintJobCount = customerJobs.Count(),
                CompletedJobCount = customerJobs.Count(job =>
                    job.Status == "COMPLETED" &&
                    (job.OutputType == "PDF_EXPORT"
                        ? job.OutputCompletedUtc != null
                        : job.OutputConfirmedUtc != null)),
                LastPrintUtc = customerJobs.Max(job => job.CreatedUtc)
            }).ToListAsync(ct);

        var statisticsByCustomer = jobStatistics.ToDictionary(item => item.CustomerId);
        return new
        {
            page,
            pageSize,
            totalCount,
            items = customers.Select(customer =>
            {
                statisticsByCustomer.TryGetValue(customer.CustomerId, out var statistics);
                return new
                {
                    id = customer.CustomerId,
                    customer.DisplayName,
                    MaskedEmail = CustomerDirectoryRules.MaskEmail(customer.Email),
                    customer.FirstSeenUtc,
                    customer.LastSeenUtc,
                    customer.SessionCount,
                    PrintJobCount = statistics?.PrintJobCount ?? 0,
                    CompletedJobCount = statistics?.CompletedJobCount ?? 0,
                    LastPrintUtc = statistics?.LastPrintUtc
                };
            }).ToList()
        };
    }

    public async Task<object?> GetCustomerJobsAsync(
        Guid tenantId,
        Guid customerId,
        int take,
        CancellationToken ct)
    {
        var customer = await db.Users.AsNoTracking()
            .Where(user => user.Id == customerId &&
                           user.TenantId == null &&
                           db.CustomerPrintSessions.Any(session =>
                               session.TenantId == tenantId && session.CustomerUserId == user.Id))
            .Select(user => new
            {
                user.Id,
                user.DisplayName,
                user.Email
            })
            .SingleOrDefaultAsync(ct);

        if (customer is null)
            return null;

        var customerJobs =
            from job in db.PrintJobs.AsNoTracking()
            join document in db.PrintDocuments.AsNoTracking()
                on new { job.TenantId, PrintJobId = job.Id }
                equals new { document.TenantId, document.PrintJobId }
            where job.TenantId == tenantId &&
                  (job.CustomerUserId == customerId ||
                   (job.CustomerSessionId != null && db.CustomerPrintSessions.Any(session =>
                       session.TenantId == tenantId &&
                       session.Id == job.CustomerSessionId &&
                       session.CustomerUserId == customerId)))
            select new
            {
                job.Id,
                job.PrivatePrinting,
                job.JobNumber,
                job.Status,
                job.CreatedUtc,
                job.PageCount,
                job.Copies,
                job.ColourMode,
                job.PaperSize,
                job.IsDuplex,
                job.PrintKind,
                job.PhotoSizeCode,
                job.PhotoCount,
                job.PhotosPerPage,
                job.PhotoUnitPriceMinorSnapshot,
                job.Currency,
                job.QuotedAmountMinor,
                job.RejectedPageNumbersJson,
                job.OutputType,
                job.OutputConfirmedUtc,
                job.OutputCompletedUtc,
                Document = new
                {
                    OriginalFileName = PrivatePrintingRules.SafeDocumentLabel(job.PrivatePrinting, document.OriginalFileName),
                    MediaType = job.PrivatePrinting ? "application/octet-stream" : document.MediaType,
                    SizeBytes = job.PrivatePrinting ? 0 : document.SizeBytes
                },
                Payment = db.PrintPayments.AsNoTracking()
                    .Where(payment => payment.TenantId == tenantId && payment.PrintJobId == job.Id)
                    .OrderByDescending(payment => payment.CreatedUtc)
                    .Select(payment => new
                    {
                        payment.Status,
                        payment.Method,
                        payment.AmountMinor,
                        payment.RefundedAmountMinor,
                        payment.Currency,
                        payment.VerifiedUtc
                    })
                    .FirstOrDefault()
            };

        var totalJobs = await customerJobs.CountAsync(ct);
        var jobs = await customerJobs
            .OrderByDescending(item => item.CreatedUtc)
            .Take(take)
            .ToListAsync(ct);

        return new
        {
            customer = new
            {
                customer.Id,
                customer.DisplayName,
                MaskedEmail = CustomerDirectoryRules.MaskEmail(customer.Email)
            },
            totalJobs,
            jobs
        };
    }
}
