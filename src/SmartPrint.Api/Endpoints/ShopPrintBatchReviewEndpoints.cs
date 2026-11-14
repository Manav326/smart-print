
using System.Security.Claims;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintBatchReviewEndpoints
{
    public static RouteGroupBuilder MapShopPrintBatchReviewEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPost("/print-batches/{batchId:guid}/review", async (
            Guid batchId,
            ApplyPrintBatchReviewRequest request,
            ClaimsPrincipal principal,
            ShopPrintBatchReviewService batchReview,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await batchReview.ApplyAsync(
                batchId,
                tenantId,
                GetActorId(principal),
                request.ReviewedJobIds,
                request.AcceptedJobIds,
                request.PdfBrandingEnabled,
                ct);
            return MapBatchReviewResult(result);
        });

        return shop;
    }

    private static IResult MapBatchReviewResult(ShopPrintBatchReviewResult result) =>
        result.Failure switch
        {
            ShopPrintBatchReviewFailure.InvalidSelection =>
                Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["review"] = ["Review between 1 and 20 files. Accepted file IDs must be part of the reviewed batch."]
                }),
            ShopPrintBatchReviewFailure.BatchCustomerSessionMismatch =>
                Results.Conflict(new
                {
                    code = "batch_customer_session_mismatch",
                    message = "This batch contains files from more than one customer session. No file decisions were changed; refresh the queue and contact support."
                }),
            ShopPrintBatchReviewFailure.BatchReviewChanged =>
                Results.Conflict(new
                {
                    code = "batch_review_changed",
                    message = "The files awaiting review changed. Refresh the full batch before applying these decisions."
                }),
            ShopPrintBatchReviewFailure.BatchPaymentAlreadyStarted =>
                Results.Conflict(new
                {
                    code = "batch_payment_already_started",
                    message = "Checkout has already started for this batch. Do not change file decisions here; use the file-level refund workflow after payment verification."
                }),
            ShopPrintBatchReviewFailure.FileHasPaymentHistory =>
                Results.Conflict(new
                {
                    code = "batch_file_has_payment_history",
                    result.JobId,
                    result.JobNumber,
                    message = "This rejected file already has an active or verified payment. Use payment verification/refund review instead of restoring it to checkout."
                }),
            ShopPrintBatchReviewFailure.FileNotReviewable =>
                Results.Conflict(new
                {
                    code = "batch_file_not_reviewable",
                    result.JobId,
                    result.JobNumber,
                    message = "A file has already moved beyond the pre-print review stage. Refresh the batch before changing its decision."
                }),
            ShopPrintBatchReviewFailure.FileHasPaymentAttempt =>
                Results.Conflict(new
                {
                    code = "batch_file_has_payment_attempt",
                    result.JobId,
                    result.JobNumber,
                    message = "This file already has an active or verified individual payment. Resolve that payment first, then refresh the batch review."
                }),
            ShopPrintBatchReviewFailure.FileRequiresRefundReview =>
                Results.Conflict(new
                {
                    code = "batch_file_requires_refund_review",
                    result.JobId,
                    result.JobNumber,
                    message = "A paid or handed-off file cannot be rejected through pre-payment review. Use its file-level refund review before printer handoff."
                }),
            ShopPrintBatchReviewFailure.IdDocumentBrandingNotAllowed =>
                Results.Conflict(new
                {
                    code = "id_document_branding_not_allowed",
                    message = "ID card sheets cannot be modified with SmartPrint PDF branding."
                }),
            ShopPrintBatchReviewFailure.ChangedConcurrently =>
                Results.Conflict(new
                {
                    code = "batch_review_changed_concurrently",
                    message = "The batch changed while the review was being applied. Refresh the batch and confirm the current file decisions."
                }),
            ShopPrintBatchReviewFailure.None =>
                Results.Ok(new
                {
                    result.BatchId,
                    result.ReviewedCount,
                    result.AcceptedCount,
                    result.RejectedCount,
                    result.QuoteRefreshRequiredCount,
                    UpdatedUtc = result.UpdatedUtc,
                    NextStep = result.QuoteRefreshRequiredCount > 0
                        ? $"The batch review was applied. {result.QuoteRefreshRequiredCount} accepted file(s) have expired quotes and must be re-quoted before checkout; rejected files remain excluded."
                        : "The batch review was applied together. Accepted files can proceed to one batch checkout; rejected files are excluded from printing."
                }),
            _ => Results.Problem()
        };
}
