using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Modules.PlatformTenants.Services;

/// <summary>
/// Owns tenant status transitions, their transaction/row-lock boundary,
/// permanent-deactivation safety checks and immutable audit record.
/// </summary>
public sealed class PlatformTenantStatusService(SmartPrintDbContext db)
{
    public async Task<TenantStatusChangeResult> ChangeAsync(
        Guid tenantId,
        string nextStatus,
        string reason,
        Guid actorUserId,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockedTenants = await db.Tenants.FromSqlInterpolated(
            $"SELECT * FROM \"Tenants\" WHERE \"Id\" = {tenantId} FOR UPDATE").ToListAsync(ct);
        var tenant = lockedTenants.SingleOrDefault();
        if (tenant is null)
        {
            await transaction.RollbackAsync(ct);
            return TenantStatusChangeResult.NotFound();
        }

        if (string.Equals(tenant.Status, nextStatus, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(ct);
            return TenantStatusChangeResult.Duplicate(tenant.Id, tenant.Status, tenant.UpdatedUtc);
        }

        if (!TenantStatusRules.CanTransition(tenant.Status, nextStatus))
        {
            await transaction.RollbackAsync(ct);
            return TenantStatusChangeResult.TransitionNotAllowed(
                tenant.Status == "Inactive"
                    ? "A permanently inactive tenant cannot be reactivated."
                    : "The requested tenant status transition is not allowed.");
        }

        if (nextStatus == "Inactive")
        {
            var unresolvedJobs = await db.PrintJobs.LongCountAsync(job =>
                job.TenantId == tenant.Id &&
                job.Status != "COMPLETED" &&
                job.Status != "REJECTED" &&
                job.Status != "PAYMENT_REJECTED" &&
                job.Status != "QUOTE_EXPIRED" &&
                job.Status != "REFUNDED", ct);
            var pendingPayments = await db.PrintPayments.LongCountAsync(payment =>
                payment.TenantId == tenant.Id &&
                (payment.Status == "AWAITING_CUSTOMER_PAYMENT" ||
                 payment.Status == "CUSTOMER_REPORTED" ||
                 payment.Status == "CASH_AWAITING_PAYMENT"), ct);
            var outstandingRejectedRefunds = await db.PrintPayments.LongCountAsync(payment =>
                payment.TenantId == tenant.Id &&
                (payment.Status == "VERIFIED" || payment.Status == "PARTIALLY_REFUNDED") &&
                payment.RefundedAmountMinor < payment.AmountMinor &&
                db.PrintJobs.Any(job => job.TenantId == tenant.Id &&
                    job.Id == payment.PrintJobId &&
                    (job.Status == "REJECTED" || job.Status == "PAYMENT_REJECTED" || job.Status == "QUOTE_EXPIRED")), ct);
            if (unresolvedJobs > 0 || pendingPayments > 0 || outstandingRejectedRefunds > 0)
            {
                await transaction.RollbackAsync(ct);
                return TenantStatusChangeResult.Blocked(unresolvedJobs, pendingPayments, outstandingRejectedRefunds);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var previousStatus = tenant.Status;
        tenant.Status = nextStatus;
        tenant.UpdatedUtc = now;
        db.TenantStatusAudits.Add(new TenantStatusAudit
        {
            TenantId = tenant.Id,
            PreviousStatus = previousStatus,
            NewStatus = tenant.Status,
            Reason = reason,
            ActorUserId = actorUserId,
            CreatedUtc = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TenantStatusChangeResult.Updated(tenant.Id, tenant.Status, tenant.UpdatedUtc);
    }
}

public enum TenantStatusChangeKind
{
    Updated,
    DuplicateRequest,
    NotFound,
    TransitionNotAllowed,
    UnresolvedPrintWork
}

public sealed record TenantStatusChangeResult(
    TenantStatusChangeKind Kind,
    Guid? TenantId = null,
    string? Status = null,
    DateTimeOffset? UpdatedUtc = null,
    string? Message = null,
    long UnresolvedJobs = 0,
    long PendingPayments = 0,
    long OutstandingRejectedRefunds = 0)
{
    public static TenantStatusChangeResult Updated(Guid id, string status, DateTimeOffset updatedUtc) =>
        new(TenantStatusChangeKind.Updated, id, status, updatedUtc);

    public static TenantStatusChangeResult Duplicate(Guid id, string status, DateTimeOffset updatedUtc) =>
        new(TenantStatusChangeKind.DuplicateRequest, id, status, updatedUtc);

    public static TenantStatusChangeResult NotFound() => new(TenantStatusChangeKind.NotFound);

    public static TenantStatusChangeResult TransitionNotAllowed(string message) =>
        new(TenantStatusChangeKind.TransitionNotAllowed, Message: message);

    public static TenantStatusChangeResult Blocked(long unresolvedJobs, long pendingPayments, long outstandingRejectedRefunds) =>
        new(TenantStatusChangeKind.UnresolvedPrintWork, UnresolvedJobs: unresolvedJobs,
            PendingPayments: pendingPayments, OutstandingRejectedRefunds: outstandingRejectedRefunds);
}
