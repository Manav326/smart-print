using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public sealed record ShopSubscriptionPaymentOrderResponse(
    string Provider,
    string OrderId,
    long AmountMinor,
    string Currency,
    string KeyId,
    string PlanCode,
    string PlanName,
    string BillingInterval);

public sealed record ShopSubscriptionPaymentVerificationResponse(
    string Status,
    Guid? SubscriptionId,
    DateTimeOffset? PeriodEndUtc,
    bool DuplicateRequest);

public sealed record RazorpayWebhookProcessingResponse(
    string Status,
    bool DuplicateEvent);

public sealed class SubscriptionPaymentException(string code, string message, int statusCode = 400)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class SubscriptionRazorpayService(
    SmartPrintDbContext db,
    HttpClient httpClient,
    IConfiguration configuration)
{
    private const string ProviderName = "razorpay";
    private string KeyId => configuration["Razorpay:KeyId"]?.Trim() ?? string.Empty;
    private string KeySecret => configuration["Razorpay:KeySecret"] ?? string.Empty;
    private string WebhookSecret => configuration["Razorpay:WebhookSecret"] ?? string.Empty;
    private string BaseUrl =>
        (configuration["Razorpay:BaseUrl"]?.Trim() ?? "https://api.razorpay.com/v1").TrimEnd('/');

    public async Task<ShopSubscriptionPaymentOrderResponse> CreateOrderAsync(
        Guid tenantId,
        Guid actorUserId,
        Guid planVersionId,
        string billingInterval,
        string clientRequestId,
        CancellationToken cancellationToken)
    {
        EnsureApiConfigured();
        var requestId = clientRequestId?.Trim() ?? string.Empty;
        var interval = billingInterval?.Trim().ToUpperInvariant() ?? string.Empty;
        if (tenantId == Guid.Empty || actorUserId == Guid.Empty ||
            planVersionId == Guid.Empty || requestId.Length is < 8 or > 100)
        {
            throw new SubscriptionPaymentException(
                "invalid_subscription_order_request",
                "Choose a plan, billing interval and valid payment request ID.");
        }

        if (interval is not ("MONTHLY" or "ANNUAL"))
        {
            throw new SubscriptionPaymentException(
                "billing_interval_not_available",
                "Choose monthly or annual billing.");
        }

        ShopSubscriptionPayment payment;
        SubscriptionPlan candidatePlan;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var lockKey = $"shop-subscription-order-create:{tenantId:N}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(5, hashtext({lockKey}))", cancellationToken);

            var existing = await db.ShopSubscriptionPayments
                .SingleOrDefaultAsync(
                    item => item.TenantId == tenantId && item.ClientRequestId == requestId,
                    cancellationToken);
            if (existing is not null)
            {
                if (existing.PlanVersionId != planVersionId ||
                    !string.Equals(existing.BillingInterval, interval, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SubscriptionPaymentException(
                        "idempotency_key_reused",
                        "This payment request ID was already used for a different plan or billing interval.",
                        StatusCodes.Status409Conflict);
                }

                if (existing.Status is "CREATING" or "CREATION_UNKNOWN")
                {
                    throw new SubscriptionPaymentException(
                        "subscription_order_confirmation_pending",
                        "SmartPrint is still confirming the Razorpay order. Refresh payment status; do not start another payment.",
                        StatusCodes.Status409Conflict);
                }

                await transaction.CommitAsync(cancellationToken);
                var existingPlanName = await db.SubscriptionPlans.AsNoTracking()
                    .Where(item => item.Code == existing.PlanCode)
                    .Select(item => item.Name)
                    .SingleOrDefaultAsync(cancellationToken) ?? existing.PlanCode;
                return ToOrderResponse(existing, existingPlanName);
            }

            var tenantStatus = await db.Tenants.AsNoTracking()
                .Where(item => item.Id == tenantId)
                .Select(item => item.Status)
                .SingleOrDefaultAsync(cancellationToken);
            if (!TenantAccessRules.IsActive(tenantStatus))
            {
                throw new SubscriptionPaymentException(
                    "tenant_inactive",
                    "This shop is inactive. Contact SmartPrint support before purchasing a subscription.",
                    StatusCodes.Status403Forbidden);
            }

            var candidate = await (
                from version in db.SubscriptionPlanVersions.AsNoTracking()
                join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
                where version.Id == planVersionId
                select new { Plan = plan, Version = version })
                .SingleOrDefaultAsync(cancellationToken);
            if (candidate is null)
            {
                throw new SubscriptionPaymentException(
                    "plan_version_not_assignable",
                    "This published plan version or billing interval is no longer available.",
                    StatusCodes.Status409Conflict);
            }

            if (!PlatformSubscriptionAssignmentRules.TryResolve(
                    candidate.Plan,
                    candidate.Version,
                    interval,
                    DateTimeOffset.UtcNow,
                    out var terms,
                    out var errorCode,
                    paymentConfirmed: true) ||
                terms is null)
            {
                throw new SubscriptionPaymentException(
                    errorCode,
                    "This published plan version or billing interval is no longer available.",
                    StatusCodes.Status409Conflict);
            }

            if (terms.AmountMinor <= 0)
            {
                throw new SubscriptionPaymentException(
                    "plan_does_not_require_payment",
                    "This plan interval does not require a Razorpay payment.",
                    StatusCodes.Status409Conflict);
            }

            if (terms.AmountMinor > RazorpaySubscriptionPaymentRules.MaximumOrderAmountMinor)
            {
                throw new SubscriptionPaymentException(
                    "subscription_amount_exceeds_provider_limit",
                    "This plan's price is above the current Razorpay checkout limit. Contact SmartPrint support for an assisted subscription purchase.",
                    StatusCodes.Status409Conflict);
            }

            // Razorpay does not provide a general cancel operation for an unpaid Order.
            // Reuse one unsettled order per shop rather than creating a second payable order.
            var pendingPayment = await db.ShopSubscriptionPayments
                .Where(item => item.TenantId == tenantId &&
                               (item.Status == "CREATING" ||
                                item.Status == "CREATION_UNKNOWN" ||
                                item.Status == "CREATED" ||
                                item.Status == "FAILED" ||
                                item.Status == "CAPTURED_REVIEW_REQUIRED"))
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (pendingPayment is not null)
            {
                if (pendingPayment.Status == "CAPTURED_REVIEW_REQUIRED")
                {
                    throw new SubscriptionPaymentException(
                        "subscription_payment_review_required",
                        "A previous subscription payment was captured and needs SmartPrint support review. Do not pay again until it is reconciled.",
                        StatusCodes.Status409Conflict);
                }

                if (pendingPayment.PlanVersionId != candidate.Version.Id ||
                    !string.Equals(pendingPayment.BillingInterval, terms.BillingInterval, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SubscriptionPaymentException(
                        "subscription_payment_pending",
                        "This shop already has an unpaid subscription order. Continue that payment first; SmartPrint cannot cancel an unpaid Razorpay order safely.",
                        StatusCodes.Status409Conflict);
                }

                if (pendingPayment.Status is "CREATING" or "CREATION_UNKNOWN")
                {
                    throw new SubscriptionPaymentException(
                        "subscription_order_confirmation_pending",
                        "SmartPrint is still confirming the existing Razorpay order. Use Continue payment or refresh status; do not create another order.",
                        StatusCodes.Status409Conflict);
                }

                await transaction.CommitAsync(cancellationToken);
                return ToOrderResponse(pendingPayment, candidate.Plan.Name);
            }

            if (!string.Equals(terms.Currency, "INR", StringComparison.OrdinalIgnoreCase))
            {
                throw new SubscriptionPaymentException(
                    "unsupported_payment_currency",
                    "Razorpay subscription checkout is currently available for INR plans only.",
                    StatusCodes.Status409Conflict);
            }

            payment = new ShopSubscriptionPayment
            {
                TenantId = tenantId,
                CreatedByUserId = actorUserId,
                PlanVersionId = candidate.Version.Id,
                PlanCode = candidate.Plan.Code,
                BillingInterval = terms.BillingInterval,
                AmountMinor = terms.AmountMinor,
                Currency = terms.Currency.ToUpperInvariant(),
                ClientRequestId = requestId,
                RazorpayOrderId = "pending_" + Guid.NewGuid().ToString("N"),
                Status = "CREATING",
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            candidatePlan = candidate.Plan;

            // Commit the durable intent before the network call. A timeout must not roll
            // back the idempotency record and permit another provider order to be created.
            db.ShopSubscriptionPayments.Add(payment);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var receipt = ReceiptFor(payment);
        JsonObject? remoteOrder = null;
        try
        {
            remoteOrder = await SendRazorpayRequestAsync(
                HttpMethod.Post,
                "/orders",
                new
                {
                    amount = payment.AmountMinor,
                    currency = payment.Currency,
                    receipt,
                    notes = new
                    {
                        tenant_id = tenantId.ToString("N"),
                        subscription_payment_id = payment.Id.ToString("N"),
                        plan_version_id = payment.PlanVersionId.ToString("N"),
                        billing_interval = payment.BillingInterval
                    }
                },
                cancellationToken);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is SubscriptionPaymentException or HttpRequestException or TaskCanceledException)
        {
            // The provider may have accepted the order even when its response was lost.
            // Look it up by the same unique receipt before deciding that creation failed.
            try
            {
                remoteOrder = await FetchOrderByReceiptAsync(payment, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                remoteOrder = null;
            }

            if (remoteOrder is null)
            {
                await MarkOrderCreationUnknownAsync(payment.Id, cancellationToken);
                throw new SubscriptionPaymentException(
                    "subscription_order_confirmation_pending",
                    "Razorpay may have received the order request, but SmartPrint could not confirm its response. No second order was created. Refresh payment status or contact support if this message remains.",
                    StatusCodes.Status502BadGateway);
            }
        }

        if (remoteOrder is null ||
            !IsValidCreatedOrder(payment, remoteOrder, out var orderId))
        {
            await MarkOrderCreationUnknownAsync(payment.Id, cancellationToken);
            throw new SubscriptionPaymentException(
                "razorpay_order_mismatch",
                "Razorpay did not return an order matching the published subscription amount. SmartPrint has held the request for reconciliation and will not create a duplicate.",
                StatusCodes.Status502BadGateway);
        }

        await PersistCreatedOrderAsync(payment.Id, orderId, cancellationToken);
        payment.RazorpayOrderId = orderId;
        payment.Status = "CREATED";
        return ToOrderResponse(payment, candidatePlan.Name);
    }

    public async Task<ShopSubscriptionPaymentOrderResponse> ResumeOrderAsync(
        Guid tenantId,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        EnsureApiConfigured();
        var payment = await db.ShopSubscriptionPayments
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.Id == paymentId,
                cancellationToken);
        if (payment is null)
        {
            throw new SubscriptionPaymentException(
                "subscription_payment_not_found",
                "The pending subscription payment was not found for this shop.",
                StatusCodes.Status404NotFound);
        }

        if (payment.Status == "CAPTURED")
        {
            throw new SubscriptionPaymentException(
                "subscription_payment_already_captured",
                "This payment is already verified. Refresh your subscription status to see the active plan.",
                StatusCodes.Status409Conflict);
        }

        if (payment.Status == "CAPTURED_REVIEW_REQUIRED")
        {
            throw new SubscriptionPaymentException(
                "subscription_payment_review_required",
                "Razorpay confirmed this payment, but SmartPrint support must review the plan assignment. Do not pay again.",
                StatusCodes.Status409Conflict);
        }

        if (payment.Status is "CREATING" or "CREATION_UNKNOWN")
        {
            JsonObject? remoteOrder;
            try
            {
                remoteOrder = await FetchOrderByReceiptAsync(payment, cancellationToken);
            }
            catch (SubscriptionPaymentException)
            {
                throw new SubscriptionPaymentException(
                    "subscription_order_confirmation_pending",
                    "Razorpay order confirmation is temporarily unavailable. Refresh status and retry Continue payment; do not start a new payment.",
                    StatusCodes.Status503ServiceUnavailable);
            }

            if (remoteOrder is null ||
                !IsValidCreatedOrder(payment, remoteOrder, out var reconciledOrderId))
            {
                await MarkOrderCreationUnknownAsync(payment.Id, cancellationToken);
                throw new SubscriptionPaymentException(
                    "subscription_order_confirmation_pending",
                    "The existing Razorpay order is still being reconciled. No new order was created. Refresh status again or contact support if it remains pending.",
                    StatusCodes.Status409Conflict);
            }

            await PersistCreatedOrderAsync(payment.Id, reconciledOrderId, cancellationToken);
            payment.RazorpayOrderId = reconciledOrderId;
            payment.Status = "CREATED";
        }

        if (payment.Status is not ("CREATED" or "FAILED"))
        {
            throw new SubscriptionPaymentException(
                "subscription_payment_not_resumable",
                "This subscription payment cannot be resumed. Contact SmartPrint support before paying again.",
                StatusCodes.Status409Conflict);
        }

        var planName = await db.SubscriptionPlans.AsNoTracking()
            .Where(item => item.Code == payment.PlanCode)
            .Select(item => item.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? payment.PlanCode;
        return ToOrderResponse(payment, planName);
    }

    public async Task<ShopSubscriptionPaymentVerificationResponse> VerifyCheckoutAsync(
        Guid tenantId,
        string orderId,
        string paymentId,
        string signature,
        CancellationToken cancellationToken)
    {
        EnsureApiConfigured();
        if (tenantId == Guid.Empty || !IsProviderIdentifier(orderId) || !IsProviderIdentifier(paymentId))
        {
            throw new SubscriptionPaymentException(
                "invalid_payment_reference",
                "The Razorpay order or payment reference is invalid.");
        }

        var initial = await db.ShopSubscriptionPayments.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.RazorpayOrderId == orderId,
                cancellationToken);
        if (initial is null)
        {
            throw new SubscriptionPaymentException(
                "subscription_payment_not_found",
                "The subscription payment order was not found for this shop.",
                StatusCodes.Status404NotFound);
        }

        if (!RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
            orderId, paymentId, signature, KeySecret))
        {
            throw new SubscriptionPaymentException(
                "invalid_razorpay_signature",
                "Razorpay payment signature verification failed.",
                StatusCodes.Status401Unauthorized);
        }

        if (initial.Status == "CAPTURED" &&
            string.Equals(initial.RazorpayPaymentId, paymentId, StringComparison.Ordinal))
        {
            var assignedPeriodEnd = initial.AssignedSubscriptionId is Guid assignedId
                ? await db.TenantSubscriptions.AsNoTracking()
                    .Where(item => item.Id == assignedId)
                    .Select(item => (DateTimeOffset?)item.PeriodEndUtc)
                    .SingleOrDefaultAsync(cancellationToken)
                : null;
            return new ShopSubscriptionPaymentVerificationResponse(
                initial.Status, initial.AssignedSubscriptionId, assignedPeriodEnd, true);
        }

        var remotePayment = await FetchPaymentAsync(paymentId, cancellationToken);
        if (!string.Equals(remotePayment.PaymentId, paymentId, StringComparison.Ordinal) ||
            !RazorpaySubscriptionPaymentRules.IsCapturedPaymentMatch(
                orderId,
                initial.AmountMinor,
                initial.Currency,
                remotePayment.PaymentId,
                remotePayment.OrderId,
                remotePayment.AmountMinor,
                remotePayment.Currency,
                remotePayment.Status))
        {
            throw new SubscriptionPaymentException(
                "payment_not_captured_or_mismatched",
                "Razorpay has not confirmed a captured payment matching this subscription order.",
                StatusCodes.Status409Conflict);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = $"shop-subscription-order:{orderId}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(6, hashtext({lockKey}))", cancellationToken);

        var payment = await db.ShopSubscriptionPayments
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.RazorpayOrderId == orderId,
                cancellationToken);
        if (payment is null)
        {
            throw new SubscriptionPaymentException(
                "subscription_payment_not_found",
                "The subscription payment order was not found for this shop.",
                StatusCodes.Status404NotFound);
        }

        var result = await ApplyCapturedPaymentAsync(payment, remotePayment, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<RazorpayWebhookProcessingResponse> ProcessWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        string signature,
        string eventId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(WebhookSecret))
        {
            throw new SubscriptionPaymentException(
                "razorpay_webhook_not_configured",
                "Razorpay webhook verification is not configured.",
                StatusCodes.Status503ServiceUnavailable);
        }

        if (rawBody.IsEmpty || rawBody.Length > RazorpaySubscriptionPaymentRules.MaximumWebhookBodyBytes)
        {
            throw new SubscriptionPaymentException(
                "invalid_webhook_body",
                "Webhook body is empty or exceeds the allowed size.",
                StatusCodes.Status413PayloadTooLarge);
        }

        if (string.IsNullOrWhiteSpace(eventId) || eventId.Trim().Length > 100)
        {
            throw new SubscriptionPaymentException(
                "invalid_webhook_event_id",
                "The Razorpay event ID header is missing or invalid.");
        }

        if (!RazorpaySubscriptionPaymentRules.VerifyWebhookSignature(rawBody.Span, signature, WebhookSecret))
        {
            throw new SubscriptionPaymentException(
                "invalid_webhook_signature",
                "Razorpay webhook signature verification failed.",
                StatusCodes.Status401Unauthorized);
        }

        if (!RazorpaySubscriptionPaymentRules.TryParseWebhook(rawBody, out var webhookPayment, out var parseError) ||
            webhookPayment is null)
        {
            throw new SubscriptionPaymentException("invalid_webhook_payload", parseError);
        }

        if (!RazorpaySubscriptionPaymentRules.IsSupportedEvent(webhookPayment.EventType))
        {
            return new RazorpayWebhookProcessingResponse("IGNORED_UNSUPPORTED_EVENT", false);
        }

        if (!IsProviderIdentifier(webhookPayment.OrderId) ||
            !IsProviderIdentifier(webhookPayment.PaymentId))
        {
            throw new SubscriptionPaymentException(
                "invalid_webhook_payment_reference",
                "Razorpay webhook contains invalid payment identifiers.");
        }

        RazorpayPaymentDetails? remotePayment = null;
        if (webhookPayment.EventType is "payment.captured" or "order.paid")
        {
            EnsureApiConfigured();
            remotePayment = await FetchPaymentAsync(webhookPayment.PaymentId, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var orderLockKey = $"shop-subscription-order:{webhookPayment.OrderId}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(6, hashtext({orderLockKey}))", cancellationToken);
        var eventLockKey = $"razorpay-webhook-event:{eventId.Trim()}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(7, hashtext({eventLockKey}))", cancellationToken);

        var duplicate = await db.RazorpayWebhookEvents.AsNoTracking()
            .AnyAsync(item => item.EventId == eventId.Trim(), cancellationToken);
        if (duplicate)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new RazorpayWebhookProcessingResponse("DUPLICATE_EVENT", true);
        }

        var now = DateTimeOffset.UtcNow;
        var eventRecord = new RazorpayWebhookEvent
        {
            EventId = eventId.Trim(),
            EventType = webhookPayment.EventType,
            RazorpayOrderId = webhookPayment.OrderId,
            RazorpayPaymentId = webhookPayment.PaymentId,
            PayloadSha256 = RazorpaySubscriptionPaymentRules.Sha256Hex(rawBody.Span),
            Status = "RECEIVED",
            ReceivedUtc = now
        };

        var payment = await db.ShopSubscriptionPayments
            .SingleOrDefaultAsync(
                item => item.RazorpayOrderId == webhookPayment.OrderId,
                cancellationToken);

        if (payment is null)
        {
            eventRecord.Status = "IGNORED_UNKNOWN_ORDER";
            eventRecord.Reason = "No SmartPrint shop-subscription payment matches this Razorpay order.";
        }
        else if (webhookPayment.EventType == "payment.failed")
        {
            if (RazorpaySubscriptionPaymentRules.ShouldMarkPaymentFailed(payment.Status))
            {
                payment.Status = "FAILED";
                payment.UpdatedUtc = now;
                eventRecord.Status = "PROCESSED_FAILED";
            }
            else
            {
                eventRecord.Status = "IGNORED_ALREADY_CAPTURED";
                eventRecord.Reason = "A later failure notification cannot downgrade a captured subscription payment.";
            }
        }
        else if (remotePayment is null ||
                 !RazorpaySubscriptionPaymentRules.IsCapturedPaymentMatch(
                     payment.RazorpayOrderId,
                     payment.AmountMinor,
                     payment.Currency,
                     remotePayment.PaymentId,
                     remotePayment.OrderId,
                     remotePayment.AmountMinor,
                     remotePayment.Currency,
                     remotePayment.Status) ||
                 !string.Equals(remotePayment.PaymentId, webhookPayment.PaymentId, StringComparison.Ordinal))
        {
            eventRecord.Status = "REVIEW_REQUIRED";
            eventRecord.Reason = "The server-side Razorpay payment lookup did not match the signed webhook and stored order.";
        }
        else
        {
            var result = await ApplyCapturedPaymentAsync(payment, remotePayment, cancellationToken);
            eventRecord.Status = result.Status == "CAPTURED_REVIEW_REQUIRED"
                ? "REVIEW_REQUIRED"
                : result.DuplicateRequest ? "PROCESSED_DUPLICATE_PAYMENT" : "PROCESSED_CAPTURED";
            eventRecord.Reason = result.Status == "CAPTURED_REVIEW_REQUIRED"
                ? "Razorpay captured the payment, but the plan version could not be activated automatically."
                : null;
        }

        eventRecord.ProcessedUtc = DateTimeOffset.UtcNow;
        db.RazorpayWebhookEvents.Add(eventRecord);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RazorpayWebhookProcessingResponse(eventRecord.Status, false);
    }

    private async Task<ShopSubscriptionPaymentVerificationResponse> ApplyCapturedPaymentAsync(
        ShopSubscriptionPayment payment,
        RazorpayPaymentDetails remotePayment,
        CancellationToken cancellationToken)
    {
        if (payment.Status == "CAPTURED")
        {
            if (!string.Equals(payment.RazorpayPaymentId, remotePayment.PaymentId, StringComparison.Ordinal))
            {
                return new ShopSubscriptionPaymentVerificationResponse(
                    "CAPTURED_REVIEW_REQUIRED", payment.AssignedSubscriptionId, payment.VerifiedUtc, true);
            }

            var existingSubscription = payment.AssignedSubscriptionId is Guid subscriptionId
                ? await db.TenantSubscriptions.AsNoTracking()
                    .Where(item => item.Id == subscriptionId)
                    .Select(item => (DateTimeOffset?)item.PeriodEndUtc)
                    .SingleOrDefaultAsync(cancellationToken)
                : null;
            return new ShopSubscriptionPaymentVerificationResponse(
                payment.Status, payment.AssignedSubscriptionId, existingSubscription, true);
        }

        if (payment.Status == "CAPTURED_REVIEW_REQUIRED")
        {
            return new ShopSubscriptionPaymentVerificationResponse(
                payment.Status, payment.AssignedSubscriptionId, null, true);
        }

        var candidate = await (
            from version in db.SubscriptionPlanVersions
            join plan in db.SubscriptionPlans on version.PlanId equals plan.Id
            where version.Id == payment.PlanVersionId
            select new { Plan = plan, Version = version })
            .SingleOrDefaultAsync(cancellationToken);
        if (candidate is null ||
            !PlatformSubscriptionAssignmentRules.TryResolve(
                candidate.Plan,
                candidate.Version,
                payment.BillingInterval,
                DateTimeOffset.UtcNow,
                out var terms,
                out _,
                paymentConfirmed: true) ||
            terms is null ||
            terms.AmountMinor != payment.AmountMinor ||
            !string.Equals(terms.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
        {
            payment.Status = "CAPTURED_REVIEW_REQUIRED";
            payment.RazorpayPaymentId = remotePayment.PaymentId;
            payment.VerifiedUtc = DateTimeOffset.UtcNow;
            payment.UpdatedUtc = payment.VerifiedUtc.Value;
            return new ShopSubscriptionPaymentVerificationResponse(
                payment.Status, null, payment.VerifiedUtc, false);
        }

        // Serialize activation with both browser confirmations, webhook deliveries and
        // platform-admin grants. This reuses the platform grant's tenant lock.
        var tenantLockKey = $"tenant-subscription:{payment.TenantId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(3, hashtext({tenantLockKey}))", cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var previousSubscription = await db.TenantSubscriptions
            .Where(item => item.TenantId == payment.TenantId)
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var previousStatus = previousSubscription?.Status;
        if (previousSubscription is not null)
        {
            previousSubscription.Status = "Superseded";
        }

        var subscription = new TenantSubscription
        {
            TenantId = payment.TenantId,
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
            TenantId = payment.TenantId,
            PreviousSubscriptionId = previousSubscription?.Id,
            PreviousStatus = previousStatus,
            AssignedSubscriptionId = subscription.Id,
            PlanVersionId = candidate.Version.Id,
            ActorUserId = payment.CreatedByUserId,
            EventType = "PAID_PLAN_ASSIGNED",
            PlanCode = candidate.Plan.Code,
            BillingInterval = terms.BillingInterval,
            Status = terms.Status,
            AmountMinor = terms.AmountMinor,
            Currency = terms.Currency,
            PeriodStartUtc = terms.PeriodStartUtc,
            PeriodEndUtc = terms.PeriodEndUtc,
            Reason = "Shop subscription paid through Razorpay Checkout.",
            PaymentReference = remotePayment.PaymentId,
            IdempotencyKey = $"SHOP-RAZORPAY-{payment.Id:N}",
            CreatedUtc = now
        });

        payment.Status = "CAPTURED";
        payment.RazorpayPaymentId = remotePayment.PaymentId;
        payment.AssignedSubscriptionId = subscription.Id;
        payment.VerifiedUtc = now;
        payment.UpdatedUtc = now;

        return new ShopSubscriptionPaymentVerificationResponse(
            payment.Status, subscription.Id, subscription.PeriodEndUtc, false);
    }

    private async Task<RazorpayPaymentDetails> FetchPaymentAsync(
        string paymentId,
        CancellationToken cancellationToken)
    {
        var response = await SendRazorpayRequestAsync(
            HttpMethod.Get, "/payments/" + Uri.EscapeDataString(paymentId), null, cancellationToken);

        return new RazorpayPaymentDetails(
            ReadString(response, "id") ?? string.Empty,
            ReadString(response, "order_id") ?? string.Empty,
            ReadInt64(response, "amount"),
            ReadString(response, "currency") ?? string.Empty,
            ReadString(response, "status") ?? string.Empty);
    }

    private async Task<JsonObject> SendRazorpayRequestAsync(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(KeyId + ":" + KeySecret));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null)
            request.Content = System.Net.Http.Json.JsonContent.Create(payload);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonObject parsed;
        try
        {
            parsed = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            parsed = new JsonObject();
        }

        if (!response.IsSuccessStatusCode)
        {
            var providerMessage = ReadString(parsed["error"] as JsonObject, "description") ??
                                  ReadString(parsed, "message");
            throw new SubscriptionPaymentException(
                "razorpay_request_failed",
                providerMessage ?? $"Razorpay returned HTTP {(int)response.StatusCode}.",
                StatusCodes.Status502BadGateway);
        }

        return parsed;
    }

    private void EnsureApiConfigured()
    {
        if (string.IsNullOrWhiteSpace(KeyId) || string.IsNullOrWhiteSpace(KeySecret))
        {
            throw new SubscriptionPaymentException(
                "razorpay_not_configured",
                "Online subscription payment is not configured. Contact SmartPrint support.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private ShopSubscriptionPaymentOrderResponse ToOrderResponse(
        ShopSubscriptionPayment payment,
        string planName) =>
        new(
            ProviderName,
            payment.RazorpayOrderId,
            payment.AmountMinor,
            payment.Currency,
            KeyId,
            payment.PlanCode,
            planName,
            payment.BillingInterval);

    private static bool IsProviderIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 100 &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static string? ReadString(JsonObject? value, string key)
    {
        try { return value?[key]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
    }

    private static long? ReadInt64(JsonObject? value, string key)
    {
        try { return value?[key]?.GetValue<long>(); }
        catch (InvalidOperationException) { return null; }
    }

    private sealed record RazorpayPaymentDetails(
        string PaymentId,
        string OrderId,
        long? AmountMinor,
        string Currency,
        string Status);
}
