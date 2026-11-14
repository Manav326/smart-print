using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Migrations;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Tests;

public sealed class RazorpaySubscriptionFlowIntegrationTests
{
    [Fact]
    public async Task CheckoutCaptureCreatesSubscriptionAndRepeatedCallbackDoesNotCreateAnother()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync();
            Assert.Equal(49900, order.AmountMinor);
            Assert.Equal("rzp_test_key", order.KeyId);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);

            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            var signature = CheckoutSignature(order.OrderId, paymentId);
            var first = await fixture.Service.VerifyCheckoutAsync(
                fixture.TenantId, order.OrderId, paymentId, signature, CancellationToken.None);
            var second = await fixture.Service.VerifyCheckoutAsync(
                fixture.TenantId, order.OrderId, paymentId, signature, CancellationToken.None);

            Assert.Equal("CAPTURED", first.Status);
            Assert.False(first.DuplicateRequest);
            Assert.True(second.DuplicateRequest);
            Assert.Equal(first.SubscriptionId, second.SubscriptionId);
            Assert.NotNull(first.PeriodEndUtc);
            Assert.Equal(2, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal(1, await fixture.Db.PlatformSubscriptionAudits.CountAsync());

            var activeSubscription = await fixture.Db.TenantSubscriptions
                .SingleAsync(item => item.Id == first.SubscriptionId);
            var previousTrial = await fixture.Db.TenantSubscriptions
                .SingleAsync(item => item.PlanCode == "TRIAL");
            var audit = await fixture.Db.PlatformSubscriptionAudits.SingleAsync();
            var payment = await fixture.Db.ShopSubscriptionPayments.SingleAsync();

            Assert.Equal("Active", activeSubscription.Status);
            Assert.Equal("Superseded", previousTrial.Status);
            Assert.Equal("Trial", audit.PreviousStatus);
            Assert.Equal("PAID_PLAN_ASSIGNED", audit.EventType);
            Assert.Equal(paymentId, audit.PaymentReference);
            Assert.Equal("CAPTURED", payment.Status);
            Assert.Equal(first.SubscriptionId, payment.AssignedSubscriptionId);
        });
    }

    [Fact]
    public async Task AmbiguousProviderCreateResponseIsReconciledByReceiptWithoutCreatingADuplicate()
    {
        await WithPostgresAsync(async fixture =>
        {
            fixture.Razorpay.LoseCreateOrderResponseAfterCreating = true;

            var order = await fixture.CreateOrderAsync("ambiguous-provider-response");

            Assert.StartsWith("order_test_", order.OrderId);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
            Assert.Equal("CREATED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
            Assert.Equal(order.OrderId, (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).RazorpayOrderId);
        });
    }

    [Fact]
    public async Task UnknownProviderCreateOutcomeCanBeResumedAfterReceiptReconciliation()
    {
        await WithPostgresAsync(async fixture =>
        {
            fixture.Razorpay.LoseCreateOrderResponseAfterCreating = true;
            fixture.Razorpay.HideOrdersFromReceiptLookup = true;

            var exception = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.CreateOrderAsync("unknown-provider-response"));

            Assert.Equal("subscription_order_confirmation_pending", exception.Code);
            var payment = await fixture.Db.ShopSubscriptionPayments.SingleAsync();
            Assert.Equal("CREATION_UNKNOWN", payment.Status);
            Assert.StartsWith("pending_", payment.RazorpayOrderId);

            fixture.Razorpay.HideOrdersFromReceiptLookup = false;
            var resumed = await fixture.Service.ResumeOrderAsync(
                fixture.TenantId, payment.Id, CancellationToken.None);

            Assert.StartsWith("order_test_", resumed.OrderId);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
            Assert.Equal("CREATED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
        });
    }

    [Fact]
    public async Task ResumeReconcilesAnAlreadyCapturedProviderPaymentInsteadOfOpeningCheckoutAgain()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync("captured-before-resume");
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            fixture.Razorpay.ReturnCapturedPaymentOnOrderLookup = true;

            var storedPayment = await fixture.Db.ShopSubscriptionPayments.SingleAsync();
            var exception = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.Service.ResumeOrderAsync(
                    fixture.TenantId,
                    storedPayment.Id,
                    CancellationToken.None));

            Assert.Equal("subscription_payment_already_captured", exception.Code);
            var payment = await fixture.Db.ShopSubscriptionPayments.SingleAsync();
            Assert.Equal("CAPTURED", payment.Status);
            Assert.Equal(paymentId, payment.RazorpayPaymentId);
            Assert.NotNull(payment.AssignedSubscriptionId);
            Assert.Equal(2, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal(1, await fixture.Db.PlatformSubscriptionAudits.CountAsync());
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
        });
    }

    [Fact]
    public async Task LateCreateResponseCannotReturnCheckoutAfterParallelResumeAlreadyCapturedTheOrder()
    {
        await WithPostgresAsync(async fixture =>
        {
            fixture.Razorpay.ReturnCapturedPaymentOnOrderLookup = true;
            await using var concurrentDb = fixture.CreateAdditionalDbContext();
            var concurrentService = fixture.CreateService(concurrentDb);
            SubscriptionPaymentException? resumeException = null;
            fixture.Razorpay.BeforeCreateOrderResponse = async paymentId =>
            {
                try
                {
                    await concurrentService.ResumeOrderAsync(
                        fixture.TenantId, paymentId, CancellationToken.None);
                }
                catch (SubscriptionPaymentException exception)
                {
                    resumeException = exception;
                }
            };

            var createException = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.CreateOrderAsync("parallel-resume-captured-order"));

            Assert.Equal("subscription_payment_already_captured", resumeException?.Code);
            Assert.Equal("subscription_payment_already_captured", createException.Code);
            var payment = await fixture.Db.ShopSubscriptionPayments.SingleAsync();
            Assert.Equal("CAPTURED", payment.Status);
            Assert.NotNull(payment.AssignedSubscriptionId);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
            Assert.Equal(2, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal(1, await fixture.Db.PlatformSubscriptionAudits.CountAsync());
        });
    }

    [Fact]
    public async Task ResumeReturnsTheExistingOrderWithoutCreatingAnotherProviderOrder()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync("resume-existing-order");
            var payment = await fixture.Db.ShopSubscriptionPayments.SingleAsync();

            var resumed = await fixture.Service.ResumeOrderAsync(
                fixture.TenantId, payment.Id, CancellationToken.None);

            Assert.Equal(order.OrderId, resumed.OrderId);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
        });
    }

    [Fact]
    public async Task SubscriptionOrderAboveOneLakhRupeesIsRejectedBeforeProviderCall()
    {
        await WithPostgresAsync(async fixture =>
        {
            var version = await fixture.Db.SubscriptionPlanVersions.SingleAsync(
                item => item.Id == fixture.PlanVersionId);
            version.MonthlyPriceMinor = RazorpaySubscriptionPaymentRules.MaximumOrderAmountMinor + 1;
            await fixture.Db.SaveChangesAsync();

            var exception = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.CreateOrderAsync("amount-over-provider-limit"));

            Assert.Equal("subscription_amount_exceeds_provider_limit", exception.Code);
            Assert.Equal(0, fixture.Razorpay.CreateOrderCalls);
            Assert.Equal(0, await fixture.Db.ShopSubscriptionPayments.CountAsync());
        });
    }

    [Fact]
    public async Task SamePlanRenewalStartsAfterUnusedCurrentPeriodInsteadOfLosingIt()
    {
        await WithPostgresAsync(async fixture =>
        {
            var trial = await fixture.Db.TenantSubscriptions.SingleAsync(
                item => item.TenantId == fixture.TenantId && item.PlanCode == "TRIAL");
            trial.Status = "Superseded";
            var preservedEnd = DateTimeOffset.UtcNow.AddDays(20);
            fixture.Db.TenantSubscriptions.Add(new TenantSubscription
            {
                TenantId = fixture.TenantId,
                PlanCode = "PRO",
                Status = "Active",
                BillingInterval = "Monthly",
                PeriodStartUtc = DateTimeOffset.UtcNow.AddDays(-10),
                PeriodEndUtc = preservedEnd,
                CreatedUtc = DateTimeOffset.UtcNow
            });
            await fixture.Db.SaveChangesAsync();

            var order = await fixture.CreateOrderAsync("same-plan-renewal");
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            var verified = await fixture.Service.VerifyCheckoutAsync(
                fixture.TenantId, order.OrderId, paymentId,
                CheckoutSignature(order.OrderId, paymentId), CancellationToken.None);

            var renewed = await fixture.Db.TenantSubscriptions.SingleAsync(
                item => item.Id == verified.SubscriptionId);

            Assert.Equal(preservedEnd, renewed.PeriodStartUtc);
            Assert.Equal(preservedEnd.AddMonths(1), renewed.PeriodEndUtc);
        });
    }

    [Fact]
    public async Task RenewalAfterExpiryStartsANewPeriodInsteadOfExtendingAnExpiredTerm()
    {
        await WithPostgresAsync(async fixture =>
        {
            var trial = await fixture.Db.TenantSubscriptions.SingleAsync(
                item => item.TenantId == fixture.TenantId && item.PlanCode == "TRIAL");
            trial.Status = "Superseded";
            var expiredEnd = DateTimeOffset.UtcNow.AddDays(-1);
            fixture.Db.TenantSubscriptions.Add(new TenantSubscription
            {
                TenantId = fixture.TenantId,
                PlanCode = "PRO",
                Status = "Active",
                BillingInterval = "Monthly",
                PeriodStartUtc = expiredEnd.AddMonths(-1),
                PeriodEndUtc = expiredEnd,
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-31)
            });
            await fixture.Db.SaveChangesAsync();

            var order = await fixture.CreateOrderAsync("expired-same-plan-renewal");
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            var verified = await fixture.Service.VerifyCheckoutAsync(
                fixture.TenantId, order.OrderId, paymentId,
                CheckoutSignature(order.OrderId, paymentId), CancellationToken.None);

            var renewed = await fixture.Db.TenantSubscriptions.SingleAsync(
                item => item.Id == verified.SubscriptionId);

            Assert.True(renewed.PeriodStartUtc > expiredEnd);
            Assert.Equal(renewed.PeriodStartUtc.AddMonths(1), renewed.PeriodEndUtc);
        });
    }

    [Fact]
    public async Task SameClientRequestIdReusesTheServerCreatedRazorpayOrder()
    {
        await WithPostgresAsync(async fixture =>
        {
            var first = await fixture.CreateOrderAsync("same-client-request-id");
            var existing = await fixture.Db.ShopSubscriptionPayments.SingleAsync();
            var exception = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.Service.CreateOrderAsync(
                    fixture.TenantId,
                    fixture.OwnerId,
                    fixture.PlanVersionId,
                    "Monthly",
                    "same-client-request-id",
                    CancellationToken.None));
            var second = await fixture.Service.ResumeOrderAsync(
                fixture.TenantId, existing.Id, CancellationToken.None);

            Assert.Equal("subscription_payment_pending", exception.Code);
            Assert.Equal(first.OrderId, second.OrderId);
            Assert.Equal(first.AmountMinor, second.AmountMinor);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
            Assert.Equal(1, await fixture.Db.ShopSubscriptionPayments.CountAsync());
        });
    }

    [Fact]
    public async Task CapturedWebhookActivatesOnceAndDuplicateEventIsAcknowledged()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync();
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            var body = WebhookBody("payment.captured", paymentId, order.OrderId, order.AmountMinor, "captured");
            var signature = WebhookSignature(body);

            var first = await fixture.Service.ProcessWebhookAsync(
                body, signature, "evt_subscription_capture_1", CancellationToken.None);
            var duplicate = await fixture.Service.ProcessWebhookAsync(
                body, signature, "evt_subscription_capture_1", CancellationToken.None);

            Assert.Equal("PROCESSED_CAPTURED", first.Status);
            Assert.True(duplicate.DuplicateEvent);
            Assert.Equal("DUPLICATE_EVENT", duplicate.Status);
            Assert.Equal(2, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal(1, await fixture.Db.PlatformSubscriptionAudits.CountAsync());
            Assert.Equal(1, await fixture.Db.ShopSubscriptionPayments.CountAsync());
            Assert.Equal(1, await fixture.Db.RazorpayWebhookEvents.CountAsync());
            Assert.Equal("CAPTURED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
        });
    }

    [Fact]
    public async Task DifferentCapturedEventsForTheSameOrderCannotActivateTwice()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync();
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            var captureBody = WebhookBody("payment.captured", paymentId, order.OrderId, order.AmountMinor, "captured");
            var orderPaidBody = WebhookBody("order.paid", paymentId, order.OrderId, order.AmountMinor, "captured");

            var capture = await fixture.Service.ProcessWebhookAsync(
                captureBody, WebhookSignature(captureBody), "evt_capture_distinct_1", CancellationToken.None);
            var orderPaid = await fixture.Service.ProcessWebhookAsync(
                orderPaidBody, WebhookSignature(orderPaidBody), "evt_order_paid_distinct_2", CancellationToken.None);

            Assert.Equal("PROCESSED_CAPTURED", capture.Status);
            Assert.Equal("PROCESSED_DUPLICATE_PAYMENT", orderPaid.Status);
            Assert.Equal(2, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal(1, await fixture.Db.PlatformSubscriptionAudits.CountAsync());
            Assert.Equal(2, await fixture.Db.RazorpayWebhookEvents.CountAsync());
        });
    }

    [Fact]
    public async Task FailedEventArrivingAfterCaptureCannotDowngradeSubscription()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync();
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            var captured = WebhookBody("payment.captured", paymentId, order.OrderId, order.AmountMinor, "captured");
            var failed = WebhookBody("payment.failed", paymentId, order.OrderId, order.AmountMinor, "failed");

            await fixture.Service.ProcessWebhookAsync(
                captured, WebhookSignature(captured), "evt_capture_before_failure", CancellationToken.None);
            var failedResult = await fixture.Service.ProcessWebhookAsync(
                failed, WebhookSignature(failed), "evt_late_failure", CancellationToken.None);

            Assert.Equal("IGNORED_ALREADY_CAPTURED", failedResult.Status);
            Assert.Equal("CAPTURED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
            Assert.Equal(2, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal(1, await fixture.Db.PlatformSubscriptionAudits.CountAsync());
        });
    }

    [Fact]
    public async Task LateCaptureCanRecoverAnOrderPreviouslyMarkedFailed()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync();
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            var failed = WebhookBody("payment.failed", paymentId, order.OrderId, order.AmountMinor, "failed");
            var captured = WebhookBody("payment.captured", paymentId, order.OrderId, order.AmountMinor, "captured");

            var failedResult = await fixture.Service.ProcessWebhookAsync(
                failed, WebhookSignature(failed), "evt_early_failed", CancellationToken.None);
            Assert.Equal("PROCESSED_FAILED", failedResult.Status);
            Assert.Equal("FAILED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
            Assert.Equal(1, await fixture.Db.TenantSubscriptions.CountAsync());

            var capturedResult = await fixture.Service.ProcessWebhookAsync(
                captured, WebhookSignature(captured), "evt_late_captured", CancellationToken.None);

            Assert.Equal("PROCESSED_CAPTURED", capturedResult.Status);
            Assert.Equal("CAPTURED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
            Assert.Equal(2, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal(1, await fixture.Db.PlatformSubscriptionAudits.CountAsync());
        });
    }

    [Fact]
    public async Task InvalidWebhookSignatureIsRejectedBeforeAnyDatabaseMutation()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync();
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            var body = WebhookBody("payment.captured", paymentId, order.OrderId, order.AmountMinor, "captured");

            var exception = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.Service.ProcessWebhookAsync(
                    body, WebhookSignature(body) + "00", "evt_invalid_signature", CancellationToken.None));

            Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
            Assert.Equal(0, await fixture.Db.RazorpayWebhookEvents.CountAsync());
            Assert.Equal("CREATED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
            Assert.Equal(1, await fixture.Db.TenantSubscriptions.CountAsync());
        });
    }

    [Fact]
    public async Task CheckoutSignatureMismatchDoesNotCallRazorpayPaymentLookup()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync();
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);

            var exception = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.Service.VerifyCheckoutAsync(
                    fixture.TenantId, order.OrderId, paymentId, "bad-signature", CancellationToken.None));

            Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
            Assert.Equal(0, fixture.Razorpay.GetPaymentCalls);
            Assert.Equal(1, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal("CREATED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
        });
    }

    [Fact]
    public async Task AuthorisedButNotCapturedPaymentDoesNotActivatePlan()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync();
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            fixture.Razorpay.PaymentStatus = "authorized";

            var exception = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.Service.VerifyCheckoutAsync(
                    fixture.TenantId, order.OrderId, paymentId,
                    CheckoutSignature(order.OrderId, paymentId), CancellationToken.None));

            Assert.Equal("payment_not_captured_or_mismatched", exception.Code);
            Assert.Equal(1, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal(0, await fixture.Db.PlatformSubscriptionAudits.CountAsync());
            Assert.Equal("CREATED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
        });
    }

    [Fact]
    public async Task AmountMismatchInProviderLookupIsRecordedForReviewButNeverActivatesPlan()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync();
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            fixture.Razorpay.ActualAmountOffsetMinor = 1;
            var body = WebhookBody("payment.captured", paymentId, order.OrderId, order.AmountMinor, "captured");

            var result = await fixture.Service.ProcessWebhookAsync(
                body, WebhookSignature(body), "evt_wrong_amount", CancellationToken.None);

            Assert.Equal("REVIEW_REQUIRED", result.Status);
            Assert.Equal("CREATED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
            Assert.Equal(1, await fixture.Db.TenantSubscriptions.CountAsync());
            Assert.Equal(0, await fixture.Db.PlatformSubscriptionAudits.CountAsync());
            Assert.Equal("REVIEW_REQUIRED", (await fixture.Db.RazorpayWebhookEvents.SingleAsync()).Status);
        });
    }

    [Fact]
    public async Task NewClientRequestIdAfterBrowserRefreshReusesTheUnsettledOrder()
    {
        await WithPostgresAsync(async fixture =>
        {
            var first = await fixture.CreateOrderAsync("request-before-refresh");
            var payment = await fixture.Db.ShopSubscriptionPayments.SingleAsync();
            var exception = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.Service.CreateOrderAsync(
                    fixture.TenantId,
                    fixture.OwnerId,
                    fixture.PlanVersionId,
                    "Monthly",
                    "request-after-refresh",
                    CancellationToken.None));
            var resumed = await fixture.Service.ResumeOrderAsync(
                fixture.TenantId, payment.Id, CancellationToken.None);

            Assert.Equal("subscription_payment_pending", exception.Code);
            Assert.Equal(first.OrderId, resumed.OrderId);
            Assert.Equal(first.AmountMinor, resumed.AmountMinor);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
            Assert.Equal(1, await fixture.Db.ShopSubscriptionPayments.CountAsync());
        });
    }

    [Fact]
    public async Task ConcurrentDifferentClientRequestIdsNeverCreateTwoProviderOrders()
    {
        await WithPostgresAsync(async fixture =>
        {
            await using var secondDb = fixture.CreateAdditionalDbContext();
            var secondService = fixture.CreateService(secondDb);

            async Task<(ShopSubscriptionPaymentOrderResponse? Order, SubscriptionPaymentException? Error)> TryCreateAsync(
                SubscriptionRazorpayService service,
                string requestId)
            {
                try
                {
                    return (await service.CreateOrderAsync(
                        fixture.TenantId, fixture.OwnerId, fixture.PlanVersionId,
                        "Monthly", requestId, CancellationToken.None), null);
                }
                catch (SubscriptionPaymentException exception)
                {
                    return (null, exception);
                }
            }

            var results = await Task.WhenAll(
                TryCreateAsync(fixture.Service, "parallel-request-one"),
                TryCreateAsync(secondService, "parallel-request-two"));

            Assert.Contains(results, result => result.Order is not null);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
            Assert.Equal(1, await fixture.Db.ShopSubscriptionPayments.CountAsync());

            var created = await fixture.Db.ShopSubscriptionPayments.SingleAsync();
            var retryException = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.Service.CreateOrderAsync(
                    fixture.TenantId, fixture.OwnerId, fixture.PlanVersionId,
                    "Monthly", "parallel-request-retry", CancellationToken.None));
            var resumed = await fixture.Service.ResumeOrderAsync(
                fixture.TenantId, created.Id, CancellationToken.None);
            Assert.Equal("subscription_payment_pending", retryException.Code);
            Assert.Equal(created.RazorpayOrderId, resumed.OrderId);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
        });
    }

    [Fact]
    public async Task DifferentPlanCannotCreateASecondOrderWhileFirstPaymentIsUnsettled()
    {
        await WithPostgresAsync(async fixture =>
        {
            await fixture.CreateOrderAsync("first-plan-request");

            var exception = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.Service.CreateOrderAsync(
                    fixture.TenantId,
                    fixture.OwnerId,
                    fixture.PlanVersionId,
                    "Annual",
                    "different-interval-request",
                    CancellationToken.None));

            Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
            Assert.Equal("subscription_payment_pending", exception.Code);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
            Assert.Equal(1, await fixture.Db.ShopSubscriptionPayments.CountAsync());
        });
    }

    [Fact]
    public async Task FailedOrderIsReusedForRetryInsteadOfCreatingAnotherChargeableOrder()
    {
        await WithPostgresAsync(async fixture =>
        {
            var order = await fixture.CreateOrderAsync("before-failed-attempt");
            var paymentId = fixture.Razorpay.PaymentIdFor(order.OrderId);
            var failedBody = WebhookBody(
                "payment.failed", paymentId, order.OrderId, order.AmountMinor, "failed");

            var failure = await fixture.Service.ProcessWebhookAsync(
                failedBody,
                WebhookSignature(failedBody),
                "evt_subscription_retryable_failure",
                CancellationToken.None);

            var payment = await fixture.Db.ShopSubscriptionPayments.SingleAsync();
            var retryConflict = await Assert.ThrowsAsync<SubscriptionPaymentException>(() =>
                fixture.Service.CreateOrderAsync(
                    fixture.TenantId,
                    fixture.OwnerId,
                    fixture.PlanVersionId,
                    "Monthly",
                    "after-failed-attempt",
                    CancellationToken.None));
            var retry = await fixture.Service.ResumeOrderAsync(
                fixture.TenantId, payment.Id, CancellationToken.None);

            Assert.Equal("PROCESSED_FAILED", failure.Status);
            Assert.Equal("subscription_payment_pending", retryConflict.Code);
            Assert.Equal(order.OrderId, retry.OrderId);
            Assert.Equal(1, fixture.Razorpay.CreateOrderCalls);
            Assert.Equal("FAILED", (await fixture.Db.ShopSubscriptionPayments.SingleAsync()).Status);
        });
    }

    private static async Task WithPostgresAsync(Func<TestFixture, Task> action)
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var fixture = await TestFixture.CreateAsync(connectionString);
        await fixture.SeedAsync();
        await action(fixture);
    }

    private static string CheckoutSignature(string orderId, string paymentId) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("api-secret"),
            Encoding.UTF8.GetBytes(orderId + "|" + paymentId))).ToLowerInvariant();

    private static string WebhookSignature(ReadOnlySpan<byte> body) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("webhook-secret"),
            body)).ToLowerInvariant();

    private static byte[] WebhookBody(
        string eventType,
        string paymentId,
        string orderId,
        long amountMinor,
        string status) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            @event = eventType,
            payload = new
            {
                payment = new
                {
                    entity = new
                    {
                        id = paymentId,
                        order_id = orderId,
                        amount = amountMinor,
                        currency = "INR",
                        status
                    }
                }
            }
        });

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly string rootConnectionString;
        private readonly string schemaName;
        private readonly HttpClient httpClient;
        private readonly IConfiguration configuration;
        public SmartPrintDbContext Db { get; }
        public FakeRazorpayHandler Razorpay { get; } = new();
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid OwnerId { get; } = Guid.NewGuid();
        public Guid PlanVersionId { get; } = Guid.NewGuid();
        public SubscriptionRazorpayService Service => new(Db, httpClient, configuration);

        private TestFixture(
            string rootConnectionString,
            string schemaName,
            SmartPrintDbContext db)
        {
            this.rootConnectionString = rootConnectionString;
            this.schemaName = schemaName;
            Db = db;
            httpClient = new HttpClient(Razorpay, disposeHandler: false);
            configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Razorpay:KeyId"] = "rzp_test_key",
                    ["Razorpay:KeySecret"] = "api-secret",
                    ["Razorpay:WebhookSecret"] = "webhook-secret",
                    ["Razorpay:BaseUrl"] = "https://razorpay.test/v1"
                })
                .Build();
        }

        public static async Task<TestFixture> CreateAsync(string rootConnectionString)
        {
            var schemaName = "sp_sub_" + Guid.NewGuid().ToString("N");
            await using (var root = new NpgsqlConnection(rootConnectionString))
            {
                await root.OpenAsync();
                await using var create = root.CreateCommand();
                create.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
                await create.ExecuteNonQueryAsync();
            }

            try
            {
                var scopedConnection = new NpgsqlConnectionStringBuilder(rootConnectionString)
                {
                    SearchPath = schemaName
                }.ConnectionString;
                var options = new DbContextOptionsBuilder<SmartPrintDbContext>()
                    .UseNpgsql(scopedConnection, npgsql => npgsql.MigrationsAssembly(
                        typeof(InitialPlatform).Assembly.GetName().Name))
                    .ReplaceService<IMigrationsIdGenerator, LegacyCompatibleMigrationsIdGenerator>()
                    .Options;
                var db = new SmartPrintDbContext(options);
                await db.Database.MigrateAsync();
                return new TestFixture(rootConnectionString, schemaName, db);
            }
            catch
            {
                await using var root = new NpgsqlConnection(rootConnectionString);
                await root.OpenAsync();
                await using var drop = root.CreateCommand();
                drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
                await drop.ExecuteNonQueryAsync();
                throw;
            }
        }

        public async Task SeedAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var tenant = new Tenant
            {
                Id = TenantId,
                Slug = "razorpay-test-" + TenantId.ToString("N")[..12],
                BusinessName = "Razorpay integration test shop",
                Status = "Active"
            };
            var owner = new AppUser
            {
                Id = OwnerId,
                TenantId = TenantId,
                UserName = "owner-" + OwnerId.ToString("N")[..12],
                NormalizedUserName = ("owner-" + OwnerId.ToString("N")[..12]).ToUpperInvariant(),
                Email = "owner-" + OwnerId.ToString("N")[..12] + "@example.test",
                NormalizedEmail = ("owner-" + OwnerId.ToString("N")[..12] + "@example.test").ToUpperInvariant(),
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                DisplayName = "Shop Owner"
            };
            var plan = new SubscriptionPlan
            {
                Code = "PRO",
                Name = "Professional",
                Description = "Integration test plan",
                IsActive = true,
                IsSystemPlan = false
            };
            var version = new SubscriptionPlanVersion
            {
                Id = PlanVersionId,
                PlanId = plan.Id,
                Version = 1,
                Status = "Published",
                Currency = "INR",
                MonthlyPriceMinor = 49900,
                AnnualPriceMinor = 499900,
                MaxPrinters = 4,
                MaxAgentDevices = 2,
                MaxQrStations = 4,
                MaxMonthlyPrintJobs = 5000,
                MaxDocumentBytes = 20 * 1024 * 1024,
                PublishedUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var trial = new TenantSubscription
            {
                TenantId = TenantId,
                PlanCode = "TRIAL",
                Status = "Trial",
                BillingInterval = "Trial",
                PeriodStartUtc = now.AddDays(-7),
                PeriodEndUtc = now.AddDays(7),
                CreatedUtc = now.AddDays(-7)
            };

            Db.AddRange(tenant, owner, plan, version, trial);
            await Db.SaveChangesAsync();
        }

        public SmartPrintDbContext CreateAdditionalDbContext()
        {
            var scopedConnection = new NpgsqlConnectionStringBuilder(rootConnectionString)
            {
                SearchPath = schemaName
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<SmartPrintDbContext>()
                .UseNpgsql(scopedConnection, npgsql => npgsql.MigrationsAssembly(
                    typeof(InitialPlatform).Assembly.GetName().Name))
                .ReplaceService<IMigrationsIdGenerator, LegacyCompatibleMigrationsIdGenerator>()
                .Options;
            return new SmartPrintDbContext(options);
        }

        public SubscriptionRazorpayService CreateService(SmartPrintDbContext context) =>
            new(context, httpClient, configuration);

        public Task<ShopSubscriptionPaymentOrderResponse> CreateOrderAsync(string? clientRequestId = null) =>
            Service.CreateOrderAsync(
                TenantId,
                OwnerId,
                PlanVersionId,
                "Monthly",
                clientRequestId ?? "test-request-" + Guid.NewGuid().ToString("N")[..12],
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            httpClient.Dispose();
            await Db.DisposeAsync();
            await using var root = new NpgsqlConnection(rootConnectionString);
            await root.OpenAsync();
            await using var drop = root.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class FakeRazorpayHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, PaymentRecord> payments = new(StringComparer.Ordinal);
        private int nextOrder;
        public int CreateOrderCalls { get; private set; }
        public int GetPaymentCalls { get; private set; }
        public string PaymentStatus { get; set; } = "captured";
        public long ActualAmountOffsetMinor { get; set; }
        public bool LoseCreateOrderResponseAfterCreating { get; set; }
        public bool HideOrdersFromReceiptLookup { get; set; }
        public bool ReturnCapturedPaymentOnOrderLookup { get; set; }
        public Func<Guid, Task>? BeforeCreateOrderResponse { get; set; }

        public string PaymentIdFor(string orderId) =>
            payments.Single(item => item.Value.OrderId == orderId).Key;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Post && path == "/v1/orders")
            {
                CreateOrderCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(body);
                var amount = document.RootElement.GetProperty("amount").GetInt64();
                var currency = document.RootElement.GetProperty("currency").GetString() ?? "INR";
                var receipt = document.RootElement.GetProperty("receipt").GetString() ?? string.Empty;
                var notes = document.RootElement.GetProperty("notes");
                var subscriptionPaymentId = notes.GetProperty("subscription_payment_id").GetString() ?? string.Empty;
                var number = Interlocked.Increment(ref nextOrder);
                var orderId = "order_test_" + number;
                var paymentId = "pay_test_" + number;
                payments[paymentId] = new PaymentRecord(orderId, amount, currency, receipt, subscriptionPaymentId);
                if (BeforeCreateOrderResponse is not null)
                    await BeforeCreateOrderResponse(Guid.Parse(subscriptionPaymentId));
                if (LoseCreateOrderResponseAfterCreating)
                    return Json(HttpStatusCode.InternalServerError, new { error = new { description = "Simulated lost create-order response." } });
                return Json(HttpStatusCode.OK, new
                {
                    id = orderId,
                    amount,
                    currency,
                    status = "created",
                    notes = new { subscription_payment_id = subscriptionPaymentId }
                });
            }

            if (request.Method == HttpMethod.Get && path == "/v1/orders")
            {
                if (HideOrdersFromReceiptLookup)
                    return Json(HttpStatusCode.OK, new { items = Array.Empty<object>() });
                var query = request.RequestUri?.Query ?? string.Empty;
                var receipt = query.StartsWith("?receipt=", StringComparison.Ordinal)
                    ? Uri.UnescapeDataString(query["?receipt=".Length..])
                    : string.Empty;
                var items = payments.Values
                    .Where(item => item.Receipt == receipt)
                    .Select(item => new
                    {
                        id = item.OrderId,
                        amount = item.AmountMinor,
                        currency = item.Currency,
                        status = "created",
                        notes = new { subscription_payment_id = item.SubscriptionPaymentId }
                    })
                    .ToArray();
                return Json(HttpStatusCode.OK, new { items });
            }

            if (request.Method == HttpMethod.Get &&
                path.StartsWith("/v1/orders/", StringComparison.Ordinal) &&
                path.EndsWith("/payments", StringComparison.Ordinal))
            {
                if (!ReturnCapturedPaymentOnOrderLookup)
                    return Json(HttpStatusCode.OK, new { items = Array.Empty<object>() });

                var orderId = Uri.UnescapeDataString(path["/v1/orders/".Length..^"/payments".Length]);
                var captured = payments.FirstOrDefault(item => item.Value.OrderId == orderId);
                var items = captured.Key is null
                    ? Array.Empty<object>()
                    : new object[]
                    {
                        new
                        {
                            id = captured.Key,
                            order_id = orderId,
                            amount = captured.Value.AmountMinor,
                            currency = captured.Value.Currency,
                            status = PaymentStatus
                        }
                    };
                return Json(HttpStatusCode.OK, new { items });
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/payments/", StringComparison.Ordinal))
            {
                GetPaymentCalls++;
                var paymentId = Uri.UnescapeDataString(path["/v1/payments/".Length..]);
                if (!payments.TryGetValue(paymentId, out var record))
                    return Json(HttpStatusCode.NotFound, new { error = new { description = "Payment not found" } });

                return Json(HttpStatusCode.OK, new
                {
                    id = paymentId,
                    order_id = record.OrderId,
                    amount = record.AmountMinor + ActualAmountOffsetMinor,
                    currency = record.Currency,
                    status = PaymentStatus
                });
            }

            return Json(HttpStatusCode.NotFound, new { message = "Unexpected test request." });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

        private sealed record PaymentRecord(
            string OrderId,
            long AmountMinor,
            string Currency,
            string Receipt,
            string SubscriptionPaymentId);
    }
}
