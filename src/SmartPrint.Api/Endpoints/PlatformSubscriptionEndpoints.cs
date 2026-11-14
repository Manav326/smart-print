using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Endpoints;

public static class PlatformSubscriptionEndpoints
{
    public static RouteGroupBuilder MapPlatformSubscriptionEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/subscriptions", async (
            string? search,
            int? page,
            int? pageSize,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (search is not null && search.Trim().Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["search"] = ["Search must be 100 characters or fewer."]
                });
            }

            var pageNumber = Math.Clamp(page ?? 1, 1, 10_000);
            var boundedPageSize = Math.Clamp(pageSize ?? 25, 1, 50);
            var query = db.Tenants.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim()}%";
                query = query.Where(tenant =>
                    EF.Functions.ILike(tenant.BusinessName, pattern) ||
                    EF.Functions.ILike(tenant.Slug, pattern));
            }

            var totalCount = await query.CountAsync(ct);
            var tenants = await query
                .OrderByDescending(tenant => tenant.CreatedUtc)
                .Skip((pageNumber - 1) * boundedPageSize)
                .Take(boundedPageSize)
                .Select(tenant => new
                {
                    tenant.Id,
                    tenant.Slug,
                    tenant.BusinessName,
                    tenant.Status,
                    tenant.CreatedUtc
                })
                .ToListAsync(ct);

            var tenantIds = tenants.Select(tenant => tenant.Id).ToArray();
            var subscriptions = await db.TenantSubscriptions.AsNoTracking()
                .Where(item => tenantIds.Contains(item.TenantId))
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id)
                .ToListAsync(ct);
            var latestSubscriptions = subscriptions
                .GroupBy(item => item.TenantId)
                .ToDictionary(group => group.Key, group => group.First());

            var versionIds = latestSubscriptions.Values
                .Where(item => item.PlanVersionId != null)
                .Select(item => item.PlanVersionId!.Value)
                .Distinct()
                .ToArray();
            var planVersions = await (
                from version in db.SubscriptionPlanVersions.AsNoTracking()
                join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
                where versionIds.Contains(version.Id)
                select new
                {
                    version.Id,
                    plan.Code,
                    plan.Name,
                    version.Version,
                    version.Status,
                    version.Currency,
                    version.MonthlyPriceMinor,
                    version.AnnualPriceMinor,
                    plan.IsActive
                })
                .ToListAsync(ct);
            var versionsById = planVersions.ToDictionary(item => item.Id);

            var now = DateTimeOffset.UtcNow;
            var items = tenants.Select(tenant =>
            {
                latestSubscriptions.TryGetValue(tenant.Id, out var subscription);
                var plan = subscription?.PlanVersionId is Guid versionId &&
                           versionsById.TryGetValue(versionId, out var resolvedPlan)
                    ? resolvedPlan
                    : null;
                return new
                {
                    tenant.Id,
                    tenant.Slug,
                    tenant.BusinessName,
                    tenant.Status,
                    tenant.CreatedUtc,
                    Subscription = subscription is null ? null : new
                    {
                        subscription.Id,
                        subscription.PlanCode,
                        status = PlanEntitlementRules.GetEffectiveSubscriptionStatus(
                            subscription.Status, subscription.PeriodEndUtc, now),
                        subscription.BillingInterval,
                        subscription.PeriodStartUtc,
                        subscription.PeriodEndUtc,
                        subscription.CreatedUtc,
                        subscription.PlanVersionId
                    },
                    Plan = plan
                };
            }).ToArray();

            return Results.Ok(new
            {
                page = pageNumber,
                pageSize = boundedPageSize,
                totalCount,
                items
            });
        });

        platform.MapPost("/tenants/{tenantId:guid}/subscriptions", async (
            Guid tenantId,
            AssignPlatformSubscriptionRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var reason = request.Reason?.Trim() ?? string.Empty;
            var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
            var paymentReference = request.PaymentReference?.Trim() ?? string.Empty;
            var validPaymentReference = paymentReference.Length is >= 4 and <= 100 &&
                paymentReference.All(character =>
                    character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
            if (tenantId == Guid.Empty ||
                request.PlanVersionId == Guid.Empty ||
                reason.Length is < 10 or > 500 ||
                idempotencyKey.Length is < 8 or > 100 ||
                (request.PaymentConfirmed && !validPaymentReference) ||
                (!request.PaymentConfirmed && paymentReference.Length != 0))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignment"] = ["Choose a plan version, provide a reason of 10–500 characters and an idempotency key of 8–100 characters."]
                });
            }

            if (!Guid.TryParse(principal.FindFirstValue("sub"), out var actorUserId))
            {
                return Results.Forbid();
            }

            var previousRequest = await db.PlatformSubscriptionAudits.AsNoTracking()
                .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
            if (previousRequest is not null)
            {
                if (previousRequest.TenantId != tenantId)
                {
                    return Results.Conflict(new { code = "idempotency_key_reused", message = "This key has already been used for a different shop." });
                }

                return Results.Ok(new
                {
                    subscriptionId = previousRequest.AssignedSubscriptionId,
                    previousSubscriptionId = previousRequest.PreviousSubscriptionId,
                    planCode = previousRequest.PlanCode,
                    status = previousRequest.Status,
                    billingInterval = previousRequest.BillingInterval,
                    periodStartUtc = previousRequest.PeriodStartUtc,
                    periodEndUtc = previousRequest.PeriodEndUtc,
                    eventType = previousRequest.EventType,
                    duplicateRequest = true
                });
            }

            var candidate = await (
                from version in db.SubscriptionPlanVersions.AsNoTracking()
                join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
                where version.Id == request.PlanVersionId
                select new { Plan = plan, Version = version })
                .SingleOrDefaultAsync(ct);
            if (candidate is null)
            {
                return Results.NotFound();
            }

            var now = DateTimeOffset.UtcNow;
            if (!PlatformSubscriptionAssignmentRules.TryResolve(
                candidate.Plan,
                candidate.Version,
                request.BillingInterval,
                now,
                out var terms,
                out var errorCode,
                request.PaymentConfirmed) || terms is null)
            {
                var message = errorCode == "paid_plan_requires_verified_payment"
                    ? "Confirm that payment was received and provide its transaction reference before granting this paid plan."
                    : errorCode == "billing_interval_not_available"
                        ? "That billing interval is not configured for this published plan version."
                        : "This published plan version is not available for manual assignment.";
                return Results.Conflict(new { code = errorCode, message });
            }

            if (terms.AmountMinor == 0 && request.PaymentConfirmed)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["paymentReference"] = ["Payment confirmation is only required for a paid plan."]
                });
            }

            var tenant = await db.Tenants.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound();
            }
            if (!string.Equals(tenant.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { code = "tenant_inactive", message = "A subscription cannot be assigned while the shop tenant is inactive." });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var tenantLockKey = $"tenant-subscription:{tenantId:N}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(3, hashtext({tenantLockKey}))", ct);
            var idempotencyLockKey = $"subscription-assignment:{idempotencyKey}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(4, hashtext({idempotencyLockKey}))", ct);

            previousRequest = await db.PlatformSubscriptionAudits.AsNoTracking()
                .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
            if (previousRequest is not null)
            {
                await transaction.RollbackAsync(ct);
                if (previousRequest.TenantId != tenantId)
                {
                    return Results.Conflict(new { code = "idempotency_key_reused", message = "This key has already been used for a different shop." });
                }
                return Results.Ok(new
                {
                    subscriptionId = previousRequest.AssignedSubscriptionId,
                    previousSubscriptionId = previousRequest.PreviousSubscriptionId,
                    planCode = previousRequest.PlanCode,
                    status = previousRequest.Status,
                    billingInterval = previousRequest.BillingInterval,
                    periodStartUtc = previousRequest.PeriodStartUtc,
                    periodEndUtc = previousRequest.PeriodEndUtc,
                    eventType = previousRequest.EventType,
                    duplicateRequest = true
                });
            }

            var previousSubscription = await db.TenantSubscriptions
                .Where(item => item.TenantId == tenantId)
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync(ct);
            var previousStatus = previousSubscription?.Status;
            if (previousSubscription is not null)
            {
                previousSubscription.Status = "Superseded";
            }

            var subscription = new TenantSubscription
            {
                TenantId = tenantId,
                PlanVersionId = candidate.Version.Id,
                PlanCode = candidate.Plan.Code,
                Status = terms.Status,
                BillingInterval = terms.BillingInterval,
                PeriodStartUtc = terms.PeriodStartUtc,
                PeriodEndUtc = terms.PeriodEndUtc,
                CreatedUtc = now
            };
            db.TenantSubscriptions.Add(subscription);
            db.PlatformSubscriptionAudits.Add(new PlatformSubscriptionAudit
            {
                TenantId = tenantId,
                PreviousSubscriptionId = previousSubscription?.Id,
                PreviousStatus = previousStatus,
                AssignedSubscriptionId = subscription.Id,
                PlanVersionId = candidate.Version.Id,
                ActorUserId = actorUserId,
                EventType = terms.EventType,
                PlanCode = candidate.Plan.Code,
                BillingInterval = terms.BillingInterval,
                Status = terms.Status,
                AmountMinor = terms.AmountMinor,
                Currency = terms.Currency,
                PeriodStartUtc = terms.PeriodStartUtc,
                PeriodEndUtc = terms.PeriodEndUtc,
                Reason = reason,
                PaymentReference = request.PaymentConfirmed ? paymentReference : null,
                IdempotencyKey = idempotencyKey,
                CreatedUtc = now
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Created($"/api/v1/platform/tenants/{tenantId}/subscription-history",
                new
                {
                    subscriptionId = subscription.Id,
                    previousSubscriptionId = previousSubscription?.Id,
                    planCode = subscription.PlanCode,
                    status = subscription.Status,
                    billingInterval = subscription.BillingInterval,
                    periodStartUtc = subscription.PeriodStartUtc,
                    periodEndUtc = subscription.PeriodEndUtc,
                    eventType = terms.EventType,
                    duplicateRequest = false
                });
        });

        platform.MapGet("/tenants/{tenantId:guid}/subscription-history", async (
            Guid tenantId,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!await db.Tenants.AnyAsync(tenant => tenant.Id == tenantId, ct))
            {
                return Results.NotFound();
            }

            var history = await db.PlatformSubscriptionAudits.AsNoTracking()
                .Where(item => item.TenantId == tenantId)
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.PreviousSubscriptionId,
                    item.PreviousStatus,
                    item.AssignedSubscriptionId,
                    item.PlanVersionId,
                    item.ActorUserId,
                    item.EventType,
                    item.PlanCode,
                    item.BillingInterval,
                    item.Status,
                    item.AmountMinor,
                    item.Currency,
                    item.PeriodStartUtc,
                    item.PeriodEndUtc,
                    item.Reason,
                    item.PaymentReference,
                    item.IdempotencyKey,
                    item.CreatedUtc
                })
                .ToListAsync(ct);
            return Results.Ok(history);
        });

        return platform;
    }
}

public sealed record AssignPlatformSubscriptionRequest(
    Guid PlanVersionId,
    string BillingInterval,
    string Reason,
    string IdempotencyKey,
    bool PaymentConfirmed = false,
    string? PaymentReference = null);
