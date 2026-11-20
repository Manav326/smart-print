using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.PlatformTenants.Services;

namespace SmartPrint.Api.Modules.PlatformTenants.Endpoints;

/// <summary>
/// HTTP adapter for platform tenant lifecycle changes.
/// Transaction, row locking, unresolved-work safeguards and audit persistence
/// are owned by <see cref="PlatformTenantStatusService"/>.
/// </summary>
public static class PlatformTenantStatusEndpoints
{
    public static RouteGroupBuilder MapPlatformTenantStatusEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapPatch("/tenants/{tenantId:guid}/status", async (
            Guid tenantId,
            ChangeTenantStatusRequest request,
            ClaimsPrincipal principal,
            PlatformTenantStatusService service,
            CancellationToken ct) =>
        {
            var nextStatus = request.Status?.Trim();
            var reason = request.Reason?.Trim();
            if (nextStatus is not ("Active" or "Suspended" or "Inactive") ||
                string.IsNullOrWhiteSpace(reason) || reason.Length is < 5 or > 500)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Choose Active, Suspended or Inactive and provide an audit reason of 5–500 characters."]
                });
            }

            if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var actorUserId))
                return Results.Forbid();

            var result = await service.ChangeAsync(tenantId, nextStatus, reason, actorUserId, ct);
            if (result.Kind == TenantStatusChangeKind.NotFound)
                return Results.NotFound();

            if (result.Kind == TenantStatusChangeKind.DuplicateRequest)
                return Results.Ok(new
                {
                    Id = result.TenantId,
                    Status = result.Status,
                    UpdatedUtc = result.UpdatedUtc,
                    DuplicateRequest = true
                });

            if (result.Kind == TenantStatusChangeKind.TransitionNotAllowed)
                return Results.Conflict(new
                {
                    code = "tenant_status_transition_not_allowed",
                    message = result.Message
                });

            if (result.Kind == TenantStatusChangeKind.UnresolvedPrintWork)
                return Results.Conflict(new
                {
                    code = "tenant_has_unresolved_print_work",
                    message = "Resolve open print jobs, pending customer payments, output-unconfirmed work and refunds for rejected paid files before permanent deactivation.",
                    unresolvedJobs = result.UnresolvedJobs,
                    pendingPayments = result.PendingPayments,
                    outstandingRejectedRefunds = result.OutstandingRejectedRefunds
                });

            return Results.Ok(new
            {
                Id = result.TenantId,
                Status = result.Status,
                UpdatedUtc = result.UpdatedUtc,
                DuplicateRequest = false
            });
        });

        return platform;
    }
}
