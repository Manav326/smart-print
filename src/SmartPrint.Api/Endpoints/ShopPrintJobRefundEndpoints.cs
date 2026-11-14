using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobRefundEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobRefundEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPost("/print-jobs/{jobId:guid}/refund-preview", async (
            Guid jobId,
            PrintRefundPreviewRequest request,
            ClaimsPrincipal principal,
            ShopPrintRefundService refunds,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var result = await refunds.PreviewAsync(jobId, tenantId, request, ct);
            switch (result.Failure)
            {
                case ShopPrintRefundFailure.NotFound:
                    return Results.NotFound();
                case ShopPrintRefundFailure.NotRefundableAfterPrintHandoff:
                    return Results.Conflict(new
                    {
                        code = "file_not_refundable_after_print_handoff",
                        message = "Page review and refunds must be finalized before the file is handed to the shop computer."
                    });
                case ShopPrintRefundFailure.VerifiedPaymentNotFound:
                    return Results.Conflict(new
                    {
                        code = "verified_payment_not_found",
                        message = "This file has no remaining verified payment to refund."
                    });
                case ShopPrintRefundFailure.InvalidPages:
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["pages"] = [result.Message ?? "The selected pages are invalid."]
                    });
            }

            var rejectedPages = result.RejectedPageNumbers!;
            return Results.Ok(new
            {
                Id = result.JobId,
                result.PageCount,
                RejectedPageNumbers = rejectedPages,
                RejectedPageCount = rejectedPages.Length,
                AcceptedPageCount = result.PageCount - rejectedPages.Length,
                result.OriginalAmountMinor,
                result.PreviouslyRefundedAmountMinor,
                result.AdditionalRefundAmountMinor,
                result.CumulativeRefundAmountMinor,
                result.RemainingPaidAmountMinor,
                result.Currency
            });
        });

        shop.MapPost("/print-jobs/{jobId:guid}/refund", async (
            Guid jobId,
            RecordPrintRefundRequest request,
            ClaimsPrincipal principal,
            ShopPrintRefundService refunds,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var reason = request.Reason?.Trim();
            var method = request.RefundMethod?.Trim().ToUpperInvariant();
            var reference = request.RefundReference?.Trim();
            var key = request.IdempotencyKey?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 3 or > 300 ||
                method is not ("UPI" or "CASH") ||
                (method == "UPI" && (string.IsNullOrWhiteSpace(reference) || reference.Length is < 4 or > 100)) ||
                reference?.Length > 100 || string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 100 ||
                !request.ConfirmedSent)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["refund"] = ["Confirm the refund was sent, choose UPI or cash, provide a reason and unique key; UPI refunds need a transaction reference."]
                });
            }

            var result = await refunds.RecordAsync(
                jobId,
                tenantId,
                GetActorId(principal),
                request,
                reason,
                method!,
                string.IsNullOrWhiteSpace(reference) ? null : reference,
                key!,
                ct);
            switch (result.Failure)
            {
                case ShopPrintRefundFailure.NotFound:
                    return Results.NotFound();
                case ShopPrintRefundFailure.NotRefundableAfterPrintHandoff:
                    return Results.Conflict(new
                    {
                        code = "file_not_refundable_after_print_handoff",
                        message = "Page rejection and refunds must be finalized before local computer handoff or printing."
                    });
                case ShopPrintRefundFailure.VerifiedPaymentNotFound:
                    return Results.Conflict(new
                    {
                        code = "verified_payment_not_found",
                        message = "This file has no remaining verified payment to refund."
                    });
                case ShopPrintRefundFailure.InvalidPages:
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["pages"] = [result.Message ?? "The selected pages are invalid."]
                    });
                case ShopPrintRefundFailure.NoNewPagesRejected:
                    return Results.Conflict(new
                    {
                        code = "no_new_pages_rejected",
                        message = "Select at least one additional page to reject."
                    });
                case ShopPrintRefundFailure.RefundAmountBelowOneMinorUnit:
                    return Results.Conflict(new
                    {
                        code = "refund_amount_below_one_minor_unit",
                        message = "The selected pages do not produce an additional refund under the original quote."
                    });
                case ShopPrintRefundFailure.IdempotencyKeyReused:
                    return Results.Conflict(new
                    {
                        code = "refund_idempotency_key_reused",
                        message = "This refund request key is already associated with another print file."
                    });
                case ShopPrintRefundFailure.ActorRequired:
                    return Results.Forbid();
                case ShopPrintRefundFailure.RecordedConcurrently:
                    return Results.Conflict(new
                    {
                        code = "refund_recorded_concurrently",
                        message = "The payment changed while this refund was recorded. Refresh and preview pages again."
                    });
            }

            var refund = result.Refund!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    refund.Id,
                    refund.PrintJobId,
                    refund.PrintPaymentId,
                    refund.AmountMinor,
                    refund.Currency,
                    refund.Method,
                    refund.Status,
                    refund.Reference,
                    refund.Reason,
                    refund.RefundedUtc,
                    DuplicateRequest = true
                });
            }

            var job = result.Job!;
            var payment = result.Payment!;
            return Results.Created(
                $"/api/v1/shop/refunds/{refund.Id}",
                new
                {
                    refund.Id,
                    refund.PrintJobId,
                    refund.PrintPaymentId,
                    job.JobNumber,
                    refund.AmountMinor,
                    refund.Currency,
                    refund.Method,
                    refund.Status,
                    refund.Reference,
                    refund.Reason,
                    refund.RefundedUtc,
                    refund.RefundedByUserId,
                    RejectedPageNumbers = result.RejectedPageNumbers,
                    CumulativeRefundAmountMinor = result.CumulativeRefundAmountMinor,
                    RemainingPaidAmountMinor = payment.AmountMinor - result.CumulativeRefundAmountMinor
                });
        });
        return shop;
    }
}
