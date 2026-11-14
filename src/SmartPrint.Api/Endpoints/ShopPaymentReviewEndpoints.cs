using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Security;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPaymentReviewEndpoints
{
    public static RouteGroupBuilder MapShopPaymentReviewEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPost("/payments/{paymentId:guid}/verify", async (
            Guid paymentId,
            ClaimsPrincipal principal,
            ShopPaymentReviewService paymentReview,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var result = await paymentReview.VerifyAsync(
                paymentId, tenantId, GetActorId(principal), ct);
            switch (result.Failure)
            {
                case ShopPaymentReviewFailure.NotFound:
                    return Results.NotFound();
                case ShopPaymentReviewFailure.BatchNotReadyForVerification:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_not_ready_for_manual_verification",
                        message = result.BatchPayment?.Method == "UPI"
                            ? "Only an active UPI batch payment can be verified. Check the full batch amount in your bank/UPI app first."
                            : "Cash batch payment can be marked received only after the full amount has been physically received."
                    });
                case ShopPaymentReviewFailure.PaymentNotReadyForVerification:
                    return Results.Conflict(new
                    {
                        code = "payment_not_ready_for_manual_verification",
                        message = result.Payment?.Method == "UPI"
                            ? "Only an active UPI payment attempt can be verified. Check the actual credit in your bank/UPI app first."
                            : "Cash can be marked received only after the shop has physically received it."
                    });
                case ShopPaymentReviewFailure.BatchAllocationsMismatch:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_allocations_mismatch",
                        message = "The batch total and per-file payment allocations do not match. Nothing was authorised or rejected; contact support before retrying."
                    });
                case ShopPaymentReviewFailure.BatchChangedConcurrently:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_changed_concurrently",
                        message = "Another shop action changed this batch payment. Refresh the payment list before trying again."
                    });
                case ShopPaymentReviewFailure.PaymentChangedConcurrently:
                    return Results.Conflict(new
                    {
                        code = "payment_changed_concurrently",
                        message = "Another shop action changed this payment. Refresh the payment list before trying again."
                    });
            }

            if (result.BatchPayment is { } batchPayment)
            {
                if (result.DuplicateRequest)
                {
                    return Results.Ok(new
                    {
                        batchPayment.Id,
                        batchPayment.Status,
                        batchPayment.VerifiedUtc,
                        DuplicateRequest = true
                    });
                }

                return Results.Ok(new
                {
                    batchPayment.Id,
                    batchPayment.Status,
                    batchPayment.VerifiedUtc,
                    batchPayment.AmountMinor,
                    batchPayment.Currency,
                    FileCount = result.FileCount,
                    NextStep = "The batch payment is verified. Each accepted file is now authorised and can be assigned to a live printer."
                });
            }

            var payment = result.Payment!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    payment.Id,
                    payment.Status,
                    payment.VerifiedUtc,
                    DuplicateRequest = true
                });
            }

            var job = result.Job!;
            return Results.Ok(new
            {
                payment.Id,
                payment.Status,
                payment.VerifiedUtc,
                JobId = job.Id,
                JobNumber = job.JobNumber,
                JobStatus = job.Status,
                NextStep = "Payment is verified. Assign this job to a live printer on its paired computer. The agent will secure the document locally before you explicitly start printing."
            });
        });

        shop.MapPost("/payments/{paymentId:guid}/reject", async (
            Guid paymentId,
            RejectPrintPaymentRequest request,
            ClaimsPrincipal principal,
            ShopPaymentReviewService paymentReview,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var reason = request.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length is < 3 or > 300)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["reason"] = ["Provide a reason between 3 and 300 characters."]
                });
            }

            var result = await paymentReview.RejectAsync(
                paymentId, tenantId, GetActorId(principal), reason, ct);
            switch (result.Failure)
            {
                case ShopPaymentReviewFailure.NotFound:
                    return Results.NotFound();
                case ShopPaymentReviewFailure.BatchNotRejectable:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_not_rejectable",
                        message = "Only a batch payment that is still awaiting payment or verification can be rejected."
                    });
                case ShopPaymentReviewFailure.PaymentNotRejectable:
                    return Results.Conflict(new
                    {
                        code = "payment_not_rejectable",
                        message = "Only a payment that is still awaiting payment or verification can be rejected."
                    });
                case ShopPaymentReviewFailure.BatchAllocationsMismatch:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_allocations_mismatch",
                        message = "The batch total and per-file payment allocations do not match. Nothing was authorised or rejected; contact support before retrying."
                    });
                case ShopPaymentReviewFailure.BatchChangedConcurrently:
                    return Results.Conflict(new
                    {
                        code = "batch_payment_changed_concurrently",
                        message = "Another shop action changed this batch payment. Refresh the payment list before trying again."
                    });
                case ShopPaymentReviewFailure.PaymentChangedConcurrently:
                    return Results.Conflict(new
                    {
                        code = "payment_changed_concurrently",
                        message = "Another shop action changed this payment. Refresh the payment list before trying again."
                    });
            }

            if (result.BatchPayment is { } batchPayment)
            {
                if (result.DuplicateRequest)
                {
                    return Results.Ok(new
                    {
                        batchPayment.Id,
                        batchPayment.Status,
                        batchPayment.RejectionReason,
                        DuplicateRequest = true
                    });
                }

                return Results.Ok(new
                {
                    batchPayment.Id,
                    batchPayment.Status,
                    batchPayment.RejectionReason,
                    batchPayment.UpdatedUtc,
                    FileCount = result.FileCount
                });
            }

            var payment = result.Payment!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    payment.Id,
                    payment.Status,
                    payment.RejectionReason,
                    DuplicateRequest = true
                });
            }

            var job = result.Job!;
            return Results.Ok(new
            {
                payment.Id,
                payment.Status,
                payment.RejectionReason,
                JobId = job.Id,
                JobStatus = job.Status,
                job.UpdatedUtc
            });
        });
        return shop;
    }
}
