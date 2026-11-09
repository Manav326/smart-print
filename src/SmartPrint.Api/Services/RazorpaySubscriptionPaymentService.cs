using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Services;

public sealed record CreateShopSubscriptionOrderRequest(
    Guid PlanVersionId,
    string BillingInterval,
    string IdempotencyKey);

public sealed record VerifyShopSubscriptionPaymentRequest(
    string OrderId,
    string PaymentId,
    string Signature);

public sealed record ShopSubscriptionOrderResponse(
    Guid PaymentId,
    string OrderId,
    long AmountMinor,
    string Currency,
    string KeyId,
    string PlanName,
    string BillingInterval,
    string Status);

public sealed record ShopSubscriptionPaymentStatusResponse(
    Guid PaymentId,
    string OrderId,
    string? PaymentIdAtGateway,
    long AmountMinor,
    string Currency,
    string PlanCode,
    string PlanName,
    string BillingInterval,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? CapturedUtc,
    DateTimeOffset? AppliedUtc,
    Guid? AppliedSubscriptionId);

public sealed record RazorpayWebhookProcessingResult(bool Duplicate, string Status);

public sealed class RazorpayPaymentException(
    string code,
    string message,
    int statusCode = StatusCodes.Status409Conflict) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class RazorpaySubscriptionPaymentService(
    SmartPrintDbContext db,
    HttpClient httpClient,
    IConfiguration configuration)
{
    private string KeyId => configuration["Razorpay:KeyId"]?.Trim() ?? string.Empty;
    private string KeySecret => configuration["Razorpay:KeySecret"]?.Trim() ?? string.Empty;
    private string WebhookSecret => configuration["Razorpay:WebhookSecret"]?.Trim() ?? string.Empty;
    private string BaseUrl => (configuration["Razorpay:BaseUrl"]?.Trim() ?? "https://api.razorpay.com/v1").TrimEnd('/');

    public bool IsCheckoutConfigured =>
        !string.IsNullOrWhiteSpace(KeyId) &&
        !string.IsNullOrWhiteSpace(KeySecret) &&
        IsWebhookConfigured;

    public bool IsWebhookConfigured => !string.IsNullOrWhiteSpace(WebhookSecret);

    public async Task<IReadOnlyList<object>> GetAvailablePlansAsync(CancellationToken ct)
    {
        var plans = await (
            from version in db.SubscriptionPlanVersions.AsNoTracking()
            join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
            where plan.IsActive && !plan.IsSystemPlan && version.Status == "Published" &&
                  plan.Code != "TRIAL" &&
                  version.Currency == "INR" &&
                  (version.MonthlyPriceMinor > 0 || version.AnnualPriceMinor > 0)
            orderby plan.Name, version.Version descending
            select new
            {
                version.Id,
                plan.Code,
                plan.Name,
                plan.Description,
                version.Version,
                version.Currency,
                version.MonthlyPriceMinor,
                version.AnnualPriceMinor,
                version.MaxPrinters,
                version.MaxAgentDevices,
                version.MaxQrStations,
                version.MaxMonthlyPrintJobs,
                version.MaxDocumentBytes,
                version.CloudAssistedPrintingEnabled,
                version.WhiteLabelEnabled,
                version.AdvancedReportsEnabled,
                version.PublishedUtc
            })
            .ToListAsync(ct);

        return plans.Cast<object>().ToArray();
    }

    public async Task<ShopSubscriptionOrderResponse> CreateOrderAsync(
        Guid tenantId,
        CreateShopSubscriptionOrderRequest request,
        CancellationToken ct)
    {
        EnsureCheckoutConfigured();
        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
        if (tenantId == Guid.Empty || request.PlanVersionId == Guid.Empty ||
            idempotencyKey.Length is < 8 or > 100 ||
            idempotencyKey.Any(char.IsControl))
        {
            throw new RazorpayPaymentException(
                "invalid_order_request",
                "Choose a published plan, a billing interval and a valid request key.",
                StatusCodes.Status400BadRequest);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockKey = $"shop-subscription-order:{tenantId:N}:{idempotencyKey}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(6, hashtext({lockKey}))", ct);

        var existing = await db.ShopSubscriptionPayments.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.TenantId == tenantId && item.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
        {
            await transaction.CommitAsync(ct);
            return ToOrderResponse(existing);
        }

        var tenant = await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tenantId, ct);
        if (tenant is null)
        {
            throw new RazorpayPaymentException("shop_not_found", "The shop could not be found.", StatusCodes.Status404NotFound);
        }
        if (!string.Equals(tenant.Status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            throw new RazorpayPaymentException("shop_inactive", "An inactive shop cannot purchase a subscription.");
        }

        var candidate = await (
            from version in db.SubscriptionPlanVersions.AsNoTracking()
            join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
            where version.Id == request.PlanVersionId
            select new { Plan = plan, Version = version })
            .SingleOrDefaultAsync(ct);
        if (candidate is null)
        {
            throw new RazorpayPaymentException("plan_version_not_found", "The selected plan version could not be found.", StatusCodes.Status404NotFound);
        }

        if (!RazorpaySubscriptionPaymentRules.TryResolvePrice(
                candidate.Plan, candidate.Version, request.BillingInterval,
                out var billingInterval, out var amountMinor, out var errorCode))
        {
            var message = errorCode switch
            {
                "free_plan_does_not_require_checkout" => "This plan does not require an online payment.",
                "unsupported_currency" => "Online subscription checkout currently supports INR plans only.",
                "amount_exceeds_gateway_limit" => "The selected plan price exceeds the configured Razorpay order limit.",
                "billing_interval_not_available" => "That billing interval is not configured for the selected plan.",
                _ => "The selected plan version is not available for online checkout."
            };
            throw new RazorpayPaymentException(errorCode, message, StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;
        var payment = new ShopSubscriptionPayment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PlanVersionId = candidate.Version.Id,
            PlanCode = candidate.Plan.Code,
            PlanName = candidate.Plan.Name,
            BillingInterval = billingInterval,
            AmountMinor = amountMinor,
            Currency = candidate.Version.Currency,
            IdempotencyKey = idempotencyKey,
            Status = "Pending",
            CreatedUtc = now,
            UpdatedUtc = now
        };

        var receipt = $"SP{payment.Id:N}";
        var payload = JsonSerializer.Serialize(new
        {
            amount = amountMinor,
            currency = payment.Currency,
            receipt,
            notes = new
            {
                purpose = "SHOP_SUBSCRIPTION",
                subscription_payment_id = payment.Id.ToString("D"),
                tenant_id = tenantId.ToString("D")
            }
        });
        using var response = await SendRazorpayRequestAsync(HttpMethod.Post, "/orders", payload, ct);
        using var responseDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = responseDocument.RootElement;
        var orderId = ReadString(root, "id");
        var returnedAmount = ReadInt64(root, "amount");
        var returnedCurrency = ReadString(root, "currency");
        if (string.IsNullOrWhiteSpace(orderId) || returnedAmount != amountMinor ||
            !string.Equals(returnedCurrency, payment.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new RazorpayPaymentException(
                "gateway_order_mismatch",
                "Razorpay returned an order that did not match the server-calculated subscription price.",
                StatusCodes.Status502BadGateway);
        }

        payment.RazorpayOrderId = orderId;
        db.ShopSubscriptionPayments.Add(payment);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToOrderResponse(payment);
    }

    public async Task<ShopSubscriptionPaymentStatusResponse> VerifyCheckoutAsync(
        Guid tenantId,
        VerifyShopSubscriptionPaymentRequest request,
        CancellationToken ct)
    {
        EnsureCheckoutConfigured();
        var orderId = request.OrderId?.Trim() ?? string.Empty;
        var paymentId = request.PaymentId?.Trim() ?? string.Empty;
        var signature = request.Signature?.Trim() ?? string.Empty;
        var payment = await db.ShopSubscriptionPayments.SingleOrDefaultAsync(item =>
            item.TenantId == tenantId && item.RazorpayOrderId == orderId, ct);
        if (payment is null)
        {
            throw new RazorpayPaymentException(
                "payment_order_not_found",
                "This Razorpay order does not belong to the signed-in shop.",
                StatusCodes.Status404NotFound);
        }

        if (!RazorpaySubscriptionPaymentRules.VerifyCheckoutSignature(
                payment.RazorpayOrderId, paymentId, signature, KeySecret))
        {
            throw new RazorpayPaymentException(
                "invalid_payment_signature",
                "Razorpay could not verify this checkout response.",
                StatusCodes.Status400BadRequest);
        }

        if (payment.Status == "Applied")
        {
            if (!string.Equals(payment.RazorpayPaymentId, paymentId, StringComparison.Ordinal))
            {
                throw new RazorpayPaymentException(
                    "order_already_applied_to_another_payment",
                    "This subscription order has already been applied using a different captured payment. Contact SmartPrint support before retrying.",
                    StatusCodes.Status409Conflict);
            }
            return ToStatusResponse(payment);
        }

        var snapshot = await GetPaymentFromRazorpayAsync(paymentId, ct);
        ValidatePaymentSnapshot(payment, snapshot, paymentId);
        await ReconcilePaymentAsync(payment.Id, snapshot, ct);
        var refreshed = await db.ShopSubscriptionPayments.AsNoTracking()
            .SingleAsync(item => item.Id == payment.Id, ct);
        return ToStatusResponse(refreshed);
    }

    public async Task<ShopSubscriptionPaymentStatusResponse?> GetPaymentStatusAsync(
        Guid tenantId,
        Guid paymentId,
        CancellationToken ct)
    {
        var payment = await db.ShopSubscriptionPayments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == paymentId && item.TenantId == tenantId, ct);
        return payment is null ? null : ToStatusResponse(payment);
    }

    public async Task<RazorpayWebhookProcessingResult> ProcessWebhookAsync(
        string rawBody,
        string signature,
        string eventId,
        CancellationToken ct)
    {
        if (!IsWebhookConfigured)
        {
            throw new RazorpayPaymentException(
                "webhook_not_configured",
                "The Razorpay subscription webhook secret is not configured.",
                StatusCodes.Status503ServiceUnavailable);
        }
        if (!RazorpaySubscriptionPaymentRules.VerifyWebhookSignature(rawBody, signature, WebhookSecret))
        {
            throw new RazorpayPaymentException(
                "invalid_webhook_signature",
                "The Razorpay webhook signature is invalid.",
                StatusCodes.Status400BadRequest);
        }
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 100 || eventId.Any(char.IsControl))
        {
            throw new RazorpayPaymentException(
                "invalid_webhook_event_id",
                "The Razorpay event identifier is missing or invalid.",
                StatusCodes.Status400BadRequest);
        }

        string eventType;
        string? orderId;
        string? paymentId;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            eventType = ReadString(root, "event") ?? string.Empty;
            var entity = root.GetProperty("payload").GetProperty("payment").GetProperty("entity");
            orderId = ReadString(entity, "order_id");
            paymentId = ReadString(entity, "id");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new RazorpayPaymentException(
                "invalid_webhook_payload",
                "The Razorpay webhook payload is not a supported payment event.",
                StatusCodes.Status400BadRequest);
        }

        if (eventType.Length is 0 or > 80)
        {
            throw new RazorpayPaymentException("invalid_webhook_event", "The Razorpay event type is invalid.", StatusCodes.Status400BadRequest);
        }

        var webhookEvent = await db.RazorpayWebhookEvents
            .SingleOrDefaultAsync(item => item.EventId == eventId, ct);
        if (webhookEvent is null)
        {
            webhookEvent = new RazorpayWebhookEvent
            {
                EventId = eventId,
                EventType = eventType,
                RazorpayOrderId = orderId,
                RazorpayPaymentId = paymentId,
                Status = "Pending",
                ReceivedUtc = DateTimeOffset.UtcNow
            };
            db.RazorpayWebhookEvents.Add(webhookEvent);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.Entry(webhookEvent).State = EntityState.Detached;
                webhookEvent = await db.RazorpayWebhookEvents
                    .SingleOrDefaultAsync(item => item.EventId == eventId, ct);
                if (webhookEvent is null)
                {
                    throw;
                }
            }
        }
        else if (!string.Equals(webhookEvent.EventType, eventType, StringComparison.Ordinal) ||
                 !string.Equals(webhookEvent.RazorpayOrderId, orderId, StringComparison.Ordinal) ||
                 !string.Equals(webhookEvent.RazorpayPaymentId, paymentId, StringComparison.Ordinal))
        {
            throw new RazorpayPaymentException(
                "webhook_event_id_conflict",
                "This Razorpay event identifier was already received with a different payment payload.",
                StatusCodes.Status409Conflict);
        }
        else if (webhookEvent.Status == "Processed")
        {
            return new RazorpayWebhookProcessingResult(true, "Processed");
        }

        try
        {
            if ((eventType is "payment.captured" or "payment.authorized") &&
                !string.IsNullOrWhiteSpace(orderId) &&
                !string.IsNullOrWhiteSpace(paymentId))
            {
                var subscriptionPayment = await db.ShopSubscriptionPayments.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.RazorpayOrderId == orderId, ct);
                if (subscriptionPayment is not null)
                {
                    var snapshot = await GetPaymentFromRazorpayAsync(paymentId, ct);
                    ValidatePaymentSnapshot(subscriptionPayment, snapshot, paymentId);
                    await ReconcilePaymentAsync(subscriptionPayment.Id, snapshot, ct);
                }
            }
            else if (eventType == "payment.failed" &&
                     !string.IsNullOrWhiteSpace(orderId))
            {
                await MarkPaymentFailedAsync(orderId, paymentId, ct);
            }

            webhookEvent.Status = "Processed";
            webhookEvent.LastError = null;
            webhookEvent.ProcessedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return new RazorpayWebhookProcessingResult(false, webhookEvent.Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The inbox row was durably saved as Pending before processing.
            // Leave it untouched so a Razorpay retry can safely re-run the event.
            throw new RazorpayPaymentException(
                "webhook_processing_failed",
                "The payment webhook was received but could not yet be reconciled. Razorpay should retry this event.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task ReconcilePaymentAsync(
        Guid paymentRecordId,
        RazorpayPaymentSnapshot snapshot,
        CancellationToken ct)
    {
        var record = await db.ShopSubscriptionPayments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == paymentRecordId, ct);
        if (record is null)
        {
            return;
        }

        if (string.Equals(snapshot.Status, "captured", StringComparison.OrdinalIgnoreCase))
        {
            await ApplyCapturedPaymentAsync(paymentRecordId, snapshot, ct);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.ShopSubscriptionPayments.FromSqlInterpolated(
                $"SELECT * FROM \"ShopSubscriptionPayments\" WHERE \"Id\" = {paymentRecordId} FOR UPDATE")
            .ToListAsync(ct);
        var payment = locked.SingleOrDefault();
        if (payment is not null)
        {
            // This context may already be tracking a stale copy from checkout verification.
            // Refresh under the row lock before deciding whether activation already occurred.
            await db.Entry(payment).ReloadAsync(ct);
        }
        if (payment is null || payment.Status is "Applied" or "Captured")
        {
            await transaction.CommitAsync(ct);
            return;
        }

        if (string.Equals(snapshot.Status, "authorized", StringComparison.OrdinalIgnoreCase) &&
            (payment.Status != "Failed" ||
             !string.Equals(payment.RazorpayPaymentId, snapshot.Id, StringComparison.Ordinal)))
        {
            payment.Status = "Authorized";
            payment.RazorpayPaymentId = snapshot.Id;
            payment.FailureCode = null;
        }
        else if (string.Equals(snapshot.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            payment.Status = "Failed";
            payment.RazorpayPaymentId = snapshot.Id;
            payment.FailureCode = "razorpay_payment_failed";
        }

        payment.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ApplyCapturedPaymentAsync(
        Guid paymentRecordId,
        RazorpayPaymentSnapshot snapshot,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockedPayments = await db.ShopSubscriptionPayments.FromSqlInterpolated(
                $"SELECT * FROM \"ShopSubscriptionPayments\" WHERE \"Id\" = {paymentRecordId} FOR UPDATE")
            .ToListAsync(ct);
        var payment = lockedPayments.SingleOrDefault();
        if (payment is null)
        {
            await transaction.RollbackAsync(ct);
            return;
        }
        // Checkout verification may have loaded this entity before waiting for the lock.
        await db.Entry(payment).ReloadAsync(ct);
        if (payment.Status == "Applied")
        {
            if (!string.Equals(payment.RazorpayPaymentId, snapshot.Id, StringComparison.Ordinal))
            {
                throw new RazorpayPaymentException(
                    "order_already_applied_to_another_payment",
                    "A second Razorpay payment was reported for an order already applied to this subscription.",
                    StatusCodes.Status409Conflict);
            }
            await transaction.CommitAsync(ct);
            return;
        }

        ValidatePaymentSnapshot(payment, snapshot, snapshot.Id);
        var tenantLockKey = $"tenant-subscription:{payment.TenantId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(3, hashtext({tenantLockKey}))", ct);

        var tenant = await db.Tenants.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == payment.TenantId, ct);
        if (tenant is null || !string.Equals(tenant.Status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            throw new RazorpayPaymentException(
                "shop_inactive_after_payment",
                "Payment is captured, but the shop is inactive. Platform support must reconcile this subscription.",
                StatusCodes.Status409Conflict);
        }

        var candidate = await (
            from version in db.SubscriptionPlanVersions.AsNoTracking()
            join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
            where version.Id == payment.PlanVersionId
            select new { Plan = plan, Version = version })
            .SingleOrDefaultAsync(ct);
        if (candidate is null || candidate.Plan.Code != payment.PlanCode ||
            candidate.Version.Currency != payment.Currency)
        {
            throw new RazorpayPaymentException(
                "purchased_plan_snapshot_unavailable",
                "Payment is captured, but the purchased plan version is unavailable for automatic activation.",
                StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;
        var periodEnd = payment.BillingInterval == "Monthly"
            ? now.AddMonths(1)
            : now.AddYears(1);
        var previousSubscription = await db.TenantSubscriptions
            .Where(item => item.TenantId == payment.TenantId)
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
            TenantId = payment.TenantId,
            PlanVersionId = payment.PlanVersionId,
            PlanCode = payment.PlanCode,
            Status = "Active",
            BillingInterval = payment.BillingInterval,
            PeriodStartUtc = now,
            PeriodEndUtc = periodEnd,
            CreatedUtc = now
        };
        db.TenantSubscriptions.Add(subscription);
        db.PlatformSubscriptionAudits.Add(new PlatformSubscriptionAudit
        {
            TenantId = payment.TenantId,
            PreviousSubscriptionId = previousSubscription?.Id,
            PreviousStatus = previousStatus,
            AssignedSubscriptionId = subscription.Id,
            PlanVersionId = payment.PlanVersionId,
            ActorUserId = null,
            EventType = "SHOP_RAZORPAY_PAID",
            PlanCode = payment.PlanCode,
            BillingInterval = payment.BillingInterval,
            Status = "Active",
            AmountMinor = payment.AmountMinor,
            Currency = payment.Currency,
            PeriodStartUtc = now,
            PeriodEndUtc = periodEnd,
            Reason = $"Shop subscription payment captured and verified by Razorpay for order {payment.RazorpayOrderId}.",
            PaymentReference = snapshot.Id,
            IdempotencyKey = $"shop-razorpay:{payment.Id:N}",
            CreatedUtc = now
        });

        payment.RazorpayPaymentId = snapshot.Id;
        payment.Status = "Applied";
        payment.AppliedSubscriptionId = subscription.Id;
        payment.CapturedUtc ??= now;
        payment.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task MarkPaymentFailedAsync(string orderId, string? paymentId, CancellationToken ct)
    {
        var payment = await db.ShopSubscriptionPayments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RazorpayOrderId == orderId, ct);
        if (payment is null)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.ShopSubscriptionPayments.FromSqlInterpolated(
                $"SELECT * FROM \"ShopSubscriptionPayments\" WHERE \"Id\" = {payment.Id} FOR UPDATE")
            .ToListAsync(ct);
        var row = locked.SingleOrDefault();
        if (row is not null)
        {
            await db.Entry(row).ReloadAsync(ct);
        }
        if (row is null || row.Status is "Applied" or "Captured" ||
            (row.RazorpayPaymentId is not null &&
             !string.Equals(row.RazorpayPaymentId, paymentId, StringComparison.Ordinal)))
        {
            await transaction.CommitAsync(ct);
            return;
        }

        row.RazorpayPaymentId = paymentId;
        row.Status = "Failed";
        row.FailureCode = "razorpay_payment_failed";
        row.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static void ValidatePaymentSnapshot(
        ShopSubscriptionPayment expected,
        RazorpayPaymentSnapshot actual,
        string expectedPaymentId)
    {
        var recognizedStatus =
            string.Equals(actual.Status, "captured", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actual.Status, "authorized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actual.Status, "failed", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(actual.Id, expectedPaymentId, StringComparison.Ordinal) || !recognizedStatus ||
            !string.Equals(actual.OrderId, expected.RazorpayOrderId, StringComparison.Ordinal) ||
            actual.AmountMinor != expected.AmountMinor ||
            !string.Equals(actual.Currency, expected.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new RazorpayPaymentException(
                "razorpay_payment_mismatch",
                "The Razorpay payment order, amount or currency does not match the server-created subscription order.",
                StatusCodes.Status409Conflict);
        }
    }

    private async Task<RazorpayPaymentSnapshot> GetPaymentFromRazorpayAsync(string paymentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(paymentId) || paymentId.Length > 100)
        {
            throw new RazorpayPaymentException("invalid_payment_id", "The Razorpay payment identifier is invalid.", StatusCodes.Status400BadRequest);
        }

        using var response = await SendRazorpayRequestAsync(HttpMethod.Get, $"/payments/{Uri.EscapeDataString(paymentId)}", null, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        return new RazorpayPaymentSnapshot(
            ReadString(root, "id") ?? string.Empty,
            ReadString(root, "order_id") ?? string.Empty,
            ReadInt64(root, "amount"),
            ReadString(root, "currency") ?? string.Empty,
            ReadString(root, "status") ?? string.Empty);
    }

    private async Task<HttpResponseMessage> SendRazorpayRequestAsync(
        HttpMethod method,
        string path,
        string? body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KeyId}:{KeySecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new RazorpayPaymentException(
                "razorpay_unavailable",
                "Razorpay could not be reached. No subscription access has been changed.",
                StatusCodes.Status503ServiceUnavailable);
        }
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new RazorpayPaymentException(
                "razorpay_request_failed",
                "Razorpay did not accept the payment request. Please retry the same payment action.",
                StatusCodes.Status502BadGateway);
        }

        return response;
    }

    private void EnsureCheckoutConfigured()
    {
        if (!IsCheckoutConfigured)
        {
            throw new RazorpayPaymentException(
                "razorpay_not_configured",
                "Razorpay key and webhook credentials must be configured before online subscription checkout can start.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private ShopSubscriptionOrderResponse ToOrderResponse(ShopSubscriptionPayment payment) =>
        new(payment.Id, payment.RazorpayOrderId, payment.AmountMinor, payment.Currency,
            KeyId, payment.PlanName, payment.BillingInterval, payment.Status);

    private static ShopSubscriptionPaymentStatusResponse ToStatusResponse(ShopSubscriptionPayment payment) =>
        new(payment.Id, payment.RazorpayOrderId, payment.RazorpayPaymentId,
            payment.AmountMinor, payment.Currency, payment.PlanCode, payment.PlanName,
            payment.BillingInterval, payment.Status, payment.CreatedUtc, payment.CapturedUtc,
            payment.Status == "Applied" ? payment.UpdatedUtc : null, payment.AppliedSubscriptionId);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;

    private sealed record RazorpayPaymentSnapshot(
        string Id,
        string OrderId,
        long? AmountMinor,
        string Currency,
        string Status);
}
