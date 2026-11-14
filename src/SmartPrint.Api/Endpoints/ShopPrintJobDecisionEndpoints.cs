using SmartPrint.Api.Services;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobDecisionEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobDecisionEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPost("/print-jobs/{jobId:guid}/approve", async (
            Guid jobId,
            ClaimsPrincipal principal,
            ShopPrintJobDecisionService decisions,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await decisions.ApproveAsync(tenantId, GetActorId(principal), jobId, ct);
            if (result.Failure == ShopPrintJobDecisionFailure.NotFound)
                return Results.NotFound();

            if (result.Failure == ShopPrintJobDecisionFailure.NotReadyForApproval)
            {
                return Results.Conflict(new
                {
                    code = "job_not_ready_for_shop_approval",
                    message = "The customer must accept a valid quote before the shop can approve this job."
                });
            }

            if (result.Failure == ShopPrintJobDecisionFailure.QuoteExpired)
            {
                return Results.Conflict(new
                {
                    code = "quote_expired",
                    message = "The customer must request a new quote before this job can proceed."
                });
            }

            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    Id = result.JobId,
                    result.JobNumber,
                    result.Status,
                    result.UpdatedUtc,
                    result.DuplicateRequest
                });
            }

            return Results.Ok(new
            {
                Id = result.JobId,
                result.JobNumber,
                result.Status,
                result.Currency,
                result.QuotedAmountMinor,
                result.UpdatedUtc,
                NextStep = result.HasConfiguredPaymentMethod
                    ? "Quote approved. The customer can now choose an enabled shop payment method. Verify payment before assigning a printer."
                    : "Quote approved, but no shop payment method is enabled. Configure UPI or cash at counter in Payments before the customer can proceed."
            });
        });

        shop.MapPost("/print-jobs/{jobId:guid}/reject", async (
            Guid jobId,
            ClaimsPrincipal principal,
            ShopPrintJobDecisionService decisions,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await decisions.RejectAsync(tenantId, GetActorId(principal), jobId, ct);
            if (result.Failure == ShopPrintJobDecisionFailure.NotFound)
                return Results.NotFound();

            if (result.Failure == ShopPrintJobDecisionFailure.NotReadyForRejection)
            {
                return Results.Conflict(new
                {
                    code = "job_not_ready_for_rejection",
                    message = "Only a file awaiting approval or checkout can be rejected here. Once batch payment starts, use the file-level refund review after payment verification."
                });
            }

            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    Id = result.JobId,
                    result.JobNumber,
                    result.Status,
                    result.DuplicateRequest
                });
            }

            return Results.Ok(new
            {
                Id = result.JobId,
                result.JobNumber,
                result.Status,
                result.UpdatedUtc
            });
        });
        return shop;
    }
}
