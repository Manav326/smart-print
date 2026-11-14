using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public enum PlatformTenantStatusFailure
{
    None,
    InvalidRequest,
    ActorNotIdentified,
    NotFound,
    TransitionNotAllowed,
    UnresolvedPrintWork
}

public sealed record PlatformTenantStatusResult(
    PlatformTenantStatusFailure Failure,
    Guid? TenantId = null,
    string? Status = null,
    DateTimeOffset? UpdatedUtc = null,
    bool DuplicateRequest = false,
    long UnresolvedJobs = 0,
    long PendingPayments = 0,
    long OutstandingRejectedRefunds = 0,
    string? PreviousStatus = null)
{
    public bool Succeeded => Failure == PlatformTenantStatusFailure.None;
}

public sealed class PlatformTenantStatusService(SmartPrintDbContext db)
{
    public async Task<PlatformTenantStatusResult> ChangeAsync(
        Guid tenantId,
        Guid? actorId,
        ChangeTenantStatusRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TenantStatusChangeRules.TryNormalize(request, out var nextStatus, out var reason))
            return new PlatformTenantStatusResult(PlatformTenantStatusFailure.InvalidRequest);

        if (actorId is not Guid actorUserId)
            return new PlatformTenantStatusResult(PlatformTenantStatusFailure.ActorNotIdentified);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockedTenants = await db.Tenants.FromSqlInterpolated(
            $"SELECT * FROM \"Tenants\" WHERE \"Id\" = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var tenant = lockedTenants.SingleOrDefault();
        if (tenant is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PlatformTenantStatusResult(PlatformTenantStatusFailure.NotFound);
        }

        if (string.Equals(tenant.Status, nextStatus, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PlatformTenantStatusResult(
                PlatformTenantStatusFailure.None,
                tenant.Id,
                tenant.Status,
                tenant.UpdatedUtc,
                DuplicateRequest: true);
        }

        if (!TenantStatusRules.CanTransition(tenant.Status, nextStatus))
        {
            var previousStatus = tenant.Status;
            await transaction.RollbackAsync(cancellationToken);
            return new PlatformTenantStatusResult(
                PlatformTenantStatusFailure.TransitionNotAllowed,
                PreviousStatus: previousStatus);
        }

        if (nextStatus == "Inactive")
        {
            var unresolvedJobs = await db.PrintJobs.LongCountAsync(job =>
                job.TenantId == tenant.Id &&
                job.Status != "COMPLETED" &&
                job.Status != "REJECTED" &&
                job.Status != "PAYMENT_REJECTED" &&
                job.Status != "QUOTE_EXPIRED" &&
                job.Status != "REFUNDED", cancellationToken);
            var pendingPayments = await db.PrintPayments.LongCountAsync(payment =>
                payment.TenantId == tenant.Id &&
                (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 payment.Status == "CUSTOMER_REPORTED" ||
                 payment.Status == "CASH_AWAITING_PAYMENT"), cancellationToken);
            var outstandingRejectedRefunds = await db.PrintPayments.LongCountAsync(payment =>
                payment.TenantId == tenant.Id &&
                (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                payment.RefundedAmountMinor < payment.AmountMinor &&
                db.PrintJobs.Any(job => job.TenantId == tenant.Id &&
                    job.Id == payment.PrintJobId &&
                    (job.Status == "REJECTED" || job.Status == "PAYMENT_REJECTED" || job.Status == "QUOTE_EXPIRED")),
                cancellationToken);
            if (unresolvedJobs > 0 || pendingPayments > 0 || outstandingRejectedRefunds > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new PlatformTenantStatusResult(
                    PlatformTenantStatusFailure.UnresolvedPrintWork,
                    UnresolvedJobs: unresolvedJobs,
                    PendingPayments: pendingPayments,
                    OutstandingRejectedRefunds: outstandingRejectedRefunds);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var priorStatus = tenant.Status;
        tenant.Status = nextStatus;
        tenant.UpdatedUtc = now;
        db.TenantStatusAudits.Add(new TenantStatusAudit
        {
            TenantId = tenant.Id,
            PreviousStatus = priorStatus,
            NewStatus = tenant.Status,
            Reason = reason,
            ActorUserId = actorUserId,
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PlatformTenantStatusResult(
            PlatformTenantStatusFailure.None,
            tenant.Id,
            tenant.Status,
            tenant.UpdatedUtc);
    }
}
