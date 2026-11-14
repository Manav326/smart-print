using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class PlatformTenantStatusEndpoints
{
    public static RouteGroupBuilder MapPlatformTenantStatusEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapPatch("/tenants/{tenantId:guid}/status", async (
            Guid tenantId,
            ChangeTenantStatusRequest request,
            ClaimsPrincipal principal,
            PlatformTenantStatusService statuses,
            CancellationToken ct) =>
        {
            var actorId = GetActorId(principal);
            var result = await statuses.ChangeAsync(tenantId, actorId, request, ct);
            if (result.Failure == PlatformTenantStatusFailure.InvalidRequest)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Choose Active, Suspended or Inactive and provide an audit reason of 5–500 characters."]
                });
            }

            if (result.Failure == PlatformTenantStatusFailure.ActorNotIdentified)
                return Results.Forbid();

            if (result.Failure == PlatformTenantStatusFailure.NotFound)
                return Results.NotFound();

            if (result.Failure == PlatformTenantStatusFailure.TransitionNotAllowed)
            {
                return Results.Conflict(new
                {
                    code = "tenant_status_transition_not_allowed",
                    message = result.PreviousStatus == "Inactive"
                        ? "A permanently inactive tenant cannot be reactivated."
                        : "The requested tenant status transition is not allowed."
                });
            }

            if (result.Failure == PlatformTenantStatusFailure.UnresolvedPrintWork)
            {
                return Results.Conflict(new
                {
                    code = "tenant_has_unresolved_print_work",
                    message = "Resolve open print jobs, pending customer payments, output-unconfirmed work and refunds for rejected paid files before permanent deactivation.",
                    unresolvedJobs = result.UnresolvedJobs,
                    pendingPayments = result.PendingPayments,
                    outstandingRejectedRefunds = result.OutstandingRejectedRefunds
                });
            }

            return Results.Ok(new
            {
                Id = result.TenantId,
                Status = result.Status,
                UpdatedUtc = result.UpdatedUtc,
                DuplicateRequest = result.DuplicateRequest
            });
        });

        return platform;
    }
}
