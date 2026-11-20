using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Modules.ShopOperations.Endpoints;

/// <summary>
/// Read-only shop customer, print-job, dashboard and event-history queries.
/// Every query is scoped to the shop tenant resolved from the authenticated principal.
/// </summary>
public static class ShopOperationsQueryEndpoints
{
    public static RouteGroupBuilder MapShopOperationsQueries(this RouteGroupBuilder shop)
    {
        shop.MapGet("/customers", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            string? search,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            if (search is not null && search.Trim().Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["search"] = ["Search must be 100 characters or fewer."]
                });
            }
        
            var pageNumber = Math.Clamp(page ?? 1, 1, 10_000);
            var boundedPageSize = Math.Clamp(pageSize ?? 20, 1, 50);
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
                .Skip((pageNumber - 1) * boundedPageSize)
                .Take(boundedPageSize)
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
            return Results.Ok(new
            {
                page = pageNumber,
                pageSize = boundedPageSize,
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
            });
        });

        shop.MapGet("/customers/{customerId:guid}/jobs", async (
            Guid customerId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            int? take,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
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
            {
                return Results.NotFound();
            }
        
            var boundedTake = Math.Clamp(take ?? 50, 1, 100);
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
                .Take(boundedTake)
                .ToListAsync(ct);
        
            return Results.Ok(new
            {
                customer = new
                {
                    customer.Id,
                    customer.DisplayName,
                    MaskedEmail = CustomerDirectoryRules.MaskEmail(customer.Email)
                },
                totalJobs,
                jobs
            });
        });

        shop.MapGet("/print-jobs", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            int? offset,
            int? take,
            string? search,
            Guid? batchId,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            // Keep paging bounded; the shop UI uses 100-row pages and an exact batch
            // lookup so a filtered queue can never silently omit sibling files.
            var boundedOffset = Math.Clamp(offset ?? 0, 0, 1_000_000);
            var boundedTake = Math.Clamp(take ?? 100, 1, 100);
            var searchTerm = search?.Trim();
            if (searchTerm is { Length: > 120 }) searchTerm = searchTerm[..120];
        
            var query =
                from job in db.PrintJobs.AsNoTracking()
                join document in db.PrintDocuments.AsNoTracking()
                    on new { job.TenantId, PrintJobId = job.Id } equals new { document.TenantId, document.PrintJobId }
                where job.TenantId == tenantId
                orderby job.CreatedUtc descending
                select new
                {
                    job.Id,
                    job.BatchId,
                    job.BatchGroupName,
                    job.BatchName,
                    job.RejectedPageNumbersJson,
                    job.PdfBrandingEnabled,
                    job.PrivatePrinting,
                    job.Origin,
                    job.CustomerUserId,
                    job.RecipientName,
                    job.RecipientPhone,
                    job.JobNumber,
                    job.Status,
                    job.PageCount,
                    job.Copies,
                    job.ColourMode,
                    job.PaperSize,
                    job.IsDuplex,
                    job.Orientation,
                    job.PagesPerSheet,
                    job.PricingVersion,
                    job.PrintKind,
                    job.PhotoSizeCode,
                    job.PhotoCount,
                job.PhotosPerPage,
                    job.PhotoUnitPriceMinorSnapshot,
                    job.Currency,
                    job.QuotedAmountMinor,
                    job.QuoteExpiresUtc,
                    job.CreatedUtc,
                    job.PrinterDeviceId,
                    PrinterName = job.PrinterNameSnapshot ?? db.PrinterDevices
                        .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                        .Select(printer => printer.DisplayName)
                        .FirstOrDefault(),
                    PrinterStatus = db.PrinterDevices.AsNoTracking()
                        .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                        .Select(printer => printer.Status)
                        .FirstOrDefault(),
                    QueueName = job.PrinterQueueNameSnapshot ?? db.PrinterDevices
                        .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                        .Select(printer => printer.QueueName)
                        .FirstOrDefault(),
                    PrinterAgentName = job.PrinterAgentNameSnapshot ?? db.AgentDevices
                        .Where(agent => agent.TenantId == tenantId &&
                            agent.Id == db.PrinterDevices
                                .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                                .Select(printer => printer.AgentDeviceId)
                                .FirstOrDefault())
                        .Select(agent => agent.DeviceName)
                        .FirstOrDefault(),
                    job.PrinterSnapshotUtc,
                    job.ClaimedByAgentDeviceId,
                    job.AgentLeaseExpiresUtc,
                    job.LocalQueueReceivedUtc,
                    job.PrintDispatchLeaseExpiresUtc,
                    job.SpoolerJobId,
                    job.SpoolerJobName,
                    job.SpoolerStatus,
                    job.SpoolerSubmittedUtc,
                    job.SpoolerObservedUtc,
                    job.OutputConfirmedUtc,
                    OutputConfirmedByName = db.PrintJobEvents.AsNoTracking()
                        .Where(item => item.TenantId == tenantId &&
                                       item.PrintJobId == job.Id &&
                                       (item.ReasonCode == "SHOP_OPERATOR_CONFIRMED_PHYSICAL_OUTPUT" || item.ReasonCode == "SHOP_OPERATOR_CONFIRMED_PHYSICAL_OUTPUT_AFTER_QUEUE_RECONCILIATION"))
                        .OrderByDescending(item => item.CreatedUtc)
                        .Select(item => db.Users
                            .Where(user => user.TenantId == tenantId &&
                                           item.ActorId != null &&
                                           user.Id == item.ActorId.Value)
                            .Select(user => user.DisplayName)
                            .FirstOrDefault())
                        .FirstOrDefault(),
                    job.FailureCode,
                    OutputMode = db.PrinterDevices
                        .Where(printer => printer.TenantId == tenantId && printer.Id == job.PrinterDeviceId)
                        .Select(printer => printer.OutputMode)
                        .FirstOrDefault(),
                    job.OutputType,
                    job.OutputFilePath,
                    job.OutputFileName,
                    job.OutputSha256,
                    job.OutputSizeBytes,
                    job.OutputCompletedUtc,
                    StorageMode = document.StorageMode,
                    PhotoSourceAvailable = !job.PrivatePrinting &&
                        (document.PhotoSourceMediaType == "image/jpeg" ||
                         document.PhotoSourceMediaType == "image/png" ||
                         (document.PhotoSourceMediaType == null &&
                          (document.MediaType == "image/jpeg" || document.MediaType == "image/png"))),
                    PhotoEditorSettingsJson = job.PrivatePrinting ? null : document.PhotoEditorSettingsJson,
                    DocumentAgentDeviceId = document.AgentDeviceId,
                    DocumentAgentDeviceName = db.AgentDevices
                        .Where(agent => agent.TenantId == tenantId && agent.Id == document.AgentDeviceId)
                        .Select(agent => agent.DeviceName)
                        .FirstOrDefault(),
                    Payment = db.PrintPayments.AsNoTracking()
                        .Where(payment => payment.TenantId == job.TenantId && payment.PrintJobId == job.Id)
                        .OrderByDescending(payment => payment.CreatedUtc)
                        .Select(payment => new
                        {
                            payment.Id,
                            payment.Status,
                            payment.AmountMinor,
                            payment.Currency,
                            payment.Method
                        })
                        .FirstOrDefault(),
                    Document = new
                    {
                        OriginalFileName = PrivatePrintingRules.SafeDocumentLabel(job.PrivatePrinting, document.OriginalFileName),
                        MediaType = job.PrivatePrinting ? "application/octet-stream" : document.MediaType,
                        SizeBytes = job.PrivatePrinting ? 0 : document.SizeBytes
                    }
                };
        
            if (batchId.HasValue)
            {
                query = query.Where(job => job.BatchId == batchId.Value);
            }
        
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var normalizedSearch = searchTerm.ToLowerInvariant();
                query = query.Where(job =>
                    job.JobNumber.ToLower().Contains(normalizedSearch) ||
                    (job.BatchName != null && job.BatchName.ToLower().Contains(normalizedSearch)) ||
                    (job.BatchGroupName != null && job.BatchGroupName.ToLower().Contains(normalizedSearch)) ||
                    (job.RecipientName != null && job.RecipientName.ToLower().Contains(normalizedSearch)) ||
                    (job.RecipientPhone != null && job.RecipientPhone.ToLower().Contains(normalizedSearch)) ||
                    (!job.PrivatePrinting && job.Document.OriginalFileName.ToLower().Contains(normalizedSearch)) ||
                    (job.PrinterName != null && job.PrinterName.ToLower().Contains(normalizedSearch)));
            }
        
            var jobs = await query
                .OrderByDescending(job => job.CreatedUtc)
                .ThenByDescending(job => job.Id)
                .Skip(boundedOffset)
                .Take(boundedTake)
                .ToListAsync(ct);
            return Results.Ok(jobs);
        });

        shop.MapGet("/reports/summary", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            TenantEntitlementService entitlements,
            int? days,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            var plan = await entitlements.GetForTenantAsync(tenantId, ct);
            if (!plan.IsAccessActive)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Your shop's SmartPrint subscription is inactive. Renew it from the Subscription page to restore access.");
            if (!plan.AdvancedReportsEnabled)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Advanced reporting is not included in this shop's current plan.");
        
            var periodDays = days ?? 30;
            if (periodDays != 7 && periodDays != 30 && periodDays != 90)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["days"] = ["Choose a reporting period of 7, 30 or 90 days."]
                });
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
        
            // File-level payment allocations remain the source of refund accounting,
            // but a batch checkout is one customer payment, not one payment per file.
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
        
            return Results.Ok(new
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
        });

        shop.MapGet("/print-jobs/{jobId:guid}/events", async (
            Guid jobId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TenantIdentity.TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }
        
            if (!await db.PrintJobs.AsNoTracking().AnyAsync(job => job.Id == jobId && job.TenantId == tenantId, ct))
            {
                return Results.NotFound();
            }
        
            var events = await db.PrintJobEvents.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.PrintJobId == jobId)
                .OrderBy(item => item.CreatedUtc)
                .Select(item => new
                {
                    item.Id,
                    item.PreviousStatus,
                    item.NewStatus,
                    item.ActorType,
                    item.ReasonCode,
                    item.CreatedUtc
                })
                .ToListAsync(ct);
            return Results.Ok(events);
        });
        return shop;
    }
}
