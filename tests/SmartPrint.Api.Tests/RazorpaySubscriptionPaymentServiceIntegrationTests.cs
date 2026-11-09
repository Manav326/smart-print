using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Tests;

public sealed class RazorpaySubscriptionPaymentServiceIntegrationTests
{
    private const string KeyId = "rzp_test_smartprint";
    private const string KeySecret = "test-key-secret";
    private const string WebhookSecret = "test-webhook-secret";

    [Fact]
    public async Task OrderUsesPublishedServerPriceAndIsIdempotentPerShop()
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var db = await CreateMigratedContextAsync(connectionString);
        var fixture = await CreatePlanAsync(db);
        var gateway = new RecordingGatewayHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/orders", request.RequestUri!.AbsolutePath);
            return JsonResponse($$"""{"id":"order_{{fixture.OrderIdSuffix}}","amount":49900,"currency":"INR"}""");
        });
        var service = CreateService(db, gateway);

        var request = new CreateShopSubscriptionOrderRequest(
            fixture.PlanVersionId, "Monthly", "request-key-123456");

        var order = await service.CreateOrderAsync(fixture.TenantId, request, CancellationToken.None);
        var duplicate = await service.CreateOrderAsync(fixture.TenantId, request, CancellationToken.None);

        Assert.Equal("order_" + fixture.OrderIdSuffix, order.OrderId);
        Assert.Equal(order.OrderId, duplicate.OrderId);
        Assert.Equal(49900, order.AmountMinor);
        Assert.Equal("INR", order.Currency);
        Assert.Equal(KeyId, order.KeyId);
        Assert.Equal(1, gateway.RequestCount);
        using var payload = JsonDocument.Parse(gateway.LastRequestBody!);
        Assert.Equal(49900, payload.RootElement.GetProperty("amount").GetInt64());
        Assert.Equal("INR", payload.RootElement.GetProperty("currency").GetString());
        Assert.Equal("SHOP_SUBSCRIPTION",
            payload.RootElement.GetProperty("notes").GetProperty("purpose").GetString());
        Assert.Equal(1, await db.ShopSubscriptionPayments.CountAsync(item => item.TenantId == fixture.TenantId));
    }

    [Fact]
    public async Task CheckoutOnlyActivatesAfterValidSignatureAndCapturedGatewaySnapshot()
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var db = await CreateMigratedContextAsync(connectionString);
        var fixture = await CreatePlanAsync(db);
        var gateway = new RecordingGatewayHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse($$"""{"id":"order_{{fixture.OrderIdSuffix}}","amount":49900,"currency":"INR"}""");
            return JsonResponse($"""{"id":"pay_test_001","order_id":"order_{{fixture.OrderIdSuffix}}","amount":49900,"currency":"INR","status":"captured"}""");
        });
        var service = CreateService(db, gateway);
        var order = await service.CreateOrderAsync(
            fixture.TenantId,
            new CreateShopSubscriptionOrderRequest(fixture.PlanVersionId, "Monthly", "checkout-key-123456"),
            CancellationToken.None);

        var invalidSignature = await Assert.ThrowsAsync<RazorpayPaymentException>(() =>
            service.VerifyCheckoutAsync(
                fixture.TenantId,
                new VerifyShopSubscriptionPaymentRequest(order.OrderId, "pay_test_001", "not-a-valid-signature"),
                CancellationToken.None));
        Assert.Equal("invalid_payment_signature", invalidSignature.Code);
        Assert.Equal(0, gateway.PaymentLookupCount);
        Assert.Equal(0, await db.TenantSubscriptions.CountAsync(item => item.TenantId == fixture.TenantId));

        var status = await service.VerifyCheckoutAsync(
            fixture.TenantId,
            new VerifyShopSubscriptionPaymentRequest(
                order.OrderId, "pay_test_001", CheckoutSignature(order.OrderId, "pay_test_001")),
            CancellationToken.None);

        Assert.Equal("Applied", status.Status);
        Assert.NotNull(status.AppliedSubscriptionId);
        Assert.Equal(1, await db.TenantSubscriptions.CountAsync(item =>
            item.TenantId == fixture.TenantId && item.Status == "Active"));
        Assert.Equal(1, await db.PlatformSubscriptionAudits.CountAsync(item =>
            item.TenantId == fixture.TenantId && item.EventType == "SHOP_RAZORPAY_PAID"));

        var duplicate = await service.VerifyCheckoutAsync(
            fixture.TenantId,
            new VerifyShopSubscriptionPaymentRequest(
                order.OrderId, "pay_test_001", CheckoutSignature(order.OrderId, "pay_test_001")),
            CancellationToken.None);

        Assert.Equal(status.AppliedSubscriptionId, duplicate.AppliedSubscriptionId);
        Assert.Equal(1, await db.TenantSubscriptions.CountAsync(item => item.TenantId == fixture.TenantId));
        Assert.Equal(1, gateway.PaymentLookupCount);
    }

    [Fact]
    public async Task CapturedWebhookVerifiesRawBodyAndDeduplicatesEventId()
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var db = await CreateMigratedContextAsync(connectionString);
        var fixture = await CreatePlanAsync(db);
        var gateway = new RecordingGatewayHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse($$"""{"id":"order_{{fixture.OrderIdSuffix}}","amount":49900,"currency":"INR"}""");
            return JsonResponse($$"""{"id":"pay_{{fixture.OrderIdSuffix}}","order_id":"order_{{fixture.OrderIdSuffix}}","amount":49900,"currency":"INR","status":"captured"}""");
        });
        var service = CreateService(db, gateway);
        var order = await service.CreateOrderAsync(
            fixture.TenantId,
            new CreateShopSubscriptionOrderRequest(fixture.PlanVersionId, "Monthly", "webhook-key-123456"),
            CancellationToken.None);
        var paymentId = "pay_" + fixture.OrderIdSuffix;
        var rawBody = $$"""{"event":"payment.captured","payload":{"payment":{"entity":{"id":"{{paymentId}}","order_id":"{{order.OrderId}}"}}}}""";

        var invalid = await Assert.ThrowsAsync<RazorpayPaymentException>(() =>
            service.ProcessWebhookAsync(rawBody, "invalid", "event-invalid-1", CancellationToken.None));
        Assert.Equal("invalid_webhook_signature", invalid.Code);
        Assert.Equal(0, await db.RazorpayWebhookEvents.CountAsync());

        var signature = WebhookSignature(rawBody);
        var first = await service.ProcessWebhookAsync(rawBody, signature, "event-captured-001", CancellationToken.None);
        var duplicate = await service.ProcessWebhookAsync(rawBody, signature, "event-captured-001", CancellationToken.None);

        Assert.False(first.Duplicate);
        Assert.Equal("Processed", first.Status);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(1, gateway.PaymentLookupCount);
        Assert.Equal(1, await db.TenantSubscriptions.CountAsync(item =>
            item.TenantId == fixture.TenantId && item.Status == "Active"));
        Assert.Equal("Processed", (await db.RazorpayWebhookEvents.SingleAsync(
            item => item.EventId == "event-captured-001")).Status);
    }

    [Fact]
    public async Task CapturedAmountMismatchNeverGrantsSubscriptionAccess()
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var db = await CreateMigratedContextAsync(connectionString);
        var fixture = await CreatePlanAsync(db);
        var gateway = new RecordingGatewayHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse($$"""{"id":"order_{{fixture.OrderIdSuffix}}","amount":49900,"currency":"INR"}""");
            return JsonResponse($$"""{"id":"pay_{{fixture.OrderIdSuffix}}","order_id":"order_{{fixture.OrderIdSuffix}}","amount":49901,"currency":"INR","status":"captured"}""");
        });
        var service = CreateService(db, gateway);
        var order = await service.CreateOrderAsync(
            fixture.TenantId,
            new CreateShopSubscriptionOrderRequest(fixture.PlanVersionId, "Monthly", "mismatch-key-12345"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RazorpayPaymentException>(() =>
            service.VerifyCheckoutAsync(
                fixture.TenantId,
                new VerifyShopSubscriptionPaymentRequest(
                    order.OrderId,
                    "pay_" + fixture.OrderIdSuffix,
                    CheckoutSignature(order.OrderId, "pay_" + fixture.OrderIdSuffix)),
                CancellationToken.None));

        Assert.Equal("razorpay_payment_mismatch", exception.Code);
        Assert.Equal(0, await db.TenantSubscriptions.CountAsync(item => item.TenantId == fixture.TenantId));
        Assert.Equal("Pending", (await db.ShopSubscriptionPayments.SingleAsync(
            item => item.Id == order.PaymentId)).Status);
    }

    [Fact]
    public async Task LateFailureWebhookCannotDowngradeAlreadyAppliedPayment()
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var db = await CreateMigratedContextAsync(connectionString);
        var fixture = await CreatePlanAsync(db);
        var gateway = new RecordingGatewayHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse($$"""{"id":"order_{{fixture.OrderIdSuffix}}","amount":49900,"currency":"INR"}""");
            return JsonResponse($$"""{"id":"pay_{{fixture.OrderIdSuffix}}","order_id":"order_{{fixture.OrderIdSuffix}}","amount":49900,"currency":"INR","status":"captured"}""");
        });
        var service = CreateService(db, gateway);
        var order = await service.CreateOrderAsync(
            fixture.TenantId,
            new CreateShopSubscriptionOrderRequest(fixture.PlanVersionId, "Monthly", "race-key-12345678"),
            CancellationToken.None);
        var paymentId = "pay_" + fixture.OrderIdSuffix;
        var capturedStatus = await service.VerifyCheckoutAsync(
            fixture.TenantId,
            new VerifyShopSubscriptionPaymentRequest(
                order.OrderId, paymentId, CheckoutSignature(order.OrderId, paymentId)),
            CancellationToken.None);
        Assert.Equal("Applied", capturedStatus.Status);

        var failedBody = $$"""{"event":"payment.failed","payload":{"payment":{"entity":{"id":"{{paymentId}}","order_id":"{{order.OrderId}}"}}}}""";
        await service.ProcessWebhookAsync(
            failedBody, WebhookSignature(failedBody), "event-late-failure-001", CancellationToken.None);

        Assert.Equal("Applied", (await db.ShopSubscriptionPayments.SingleAsync(
            item => item.Id == order.PaymentId)).Status);
        Assert.Equal(1, await db.TenantSubscriptions.CountAsync(item =>
            item.TenantId == fixture.TenantId && item.Status == "Active"));
    }

    [Fact]
    public async Task AnotherShopCannotVerifyAnOrderThatDoesNotBelongToIt()
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var db = await CreateMigratedContextAsync(connectionString);
        var owner = await CreatePlanAsync(db);
        var otherShop = await CreatePlanAsync(db);
        var gateway = new RecordingGatewayHandler((request, _) =>
            JsonResponse($$"""{"id":"order_{{owner.OrderIdSuffix}}","amount":49900,"currency":"INR"}"""));
        var service = CreateService(db, gateway);
        var order = await service.CreateOrderAsync(
            owner.TenantId,
            new CreateShopSubscriptionOrderRequest(owner.PlanVersionId, "Monthly", "owner-key-123456"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<RazorpayPaymentException>(() =>
            service.VerifyCheckoutAsync(
                otherShop.TenantId,
                new VerifyShopSubscriptionPaymentRequest(
                    order.OrderId, "pay_owner", CheckoutSignature(order.OrderId, "pay_owner")),
                CancellationToken.None));

        Assert.Equal("payment_order_not_found", exception.Code);
        Assert.Equal(0, gateway.PaymentLookupCount);
    }

    private static async Task<SmartPrintDbContext> CreateMigratedContextAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SmartPrintDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(InitialPlatform).Assembly.GetName().Name))
            .ReplaceService<IMigrationsIdGenerator, LegacyCompatibleMigrationsIdGenerator>()
            .Options;
        var db = new SmartPrintDbContext(options);
        await db.Database.MigrateAsync();
        return db;
    }

    private static async Task<TestPlanFixture> CreatePlanAsync(SmartPrintDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var tenant = new Tenant
        {
            Slug = $"razorpay-{suffix}",
            BusinessName = "Razorpay integration test shop"
        };
        var plan = new SubscriptionPlan
        {
            Code = $"RZPY_{suffix.ToUpperInvariant()}",
            Name = "Integration test plan",
            IsActive = true
        };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Version = 1,
            Status = "Published",
            Currency = "INR",
            MonthlyPriceMinor = 49900,
            AnnualPriceMinor = 499900
        };
        db.AddRange(tenant, plan, version);
        await db.SaveChangesAsync();
        return new TestPlanFixture(tenant.Id, version.Id, suffix);
    }

    private static RazorpaySubscriptionPaymentService CreateService(
        SmartPrintDbContext db,
        RecordingGatewayHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Razorpay:KeyId"] = KeyId,
                ["Razorpay:KeySecret"] = KeySecret,
                ["Razorpay:WebhookSecret"] = WebhookSecret,
                ["Razorpay:BaseUrl"] = "https://razorpay.test/v1"
            })
            .Build();
        return new RazorpaySubscriptionPaymentService(db, new HttpClient(handler), configuration);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string CheckoutSignature(string orderId, string paymentId) =>
        Hmac($"{orderId}|{paymentId}", KeySecret);

    private static string WebhookSignature(string rawBody) =>
        Hmac(rawBody, WebhookSecret);

    private static string Hmac(string message, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }

    private sealed record TestPlanFixture(Guid TenantId, Guid PlanVersionId, string OrderIdSuffix);

    private sealed class RecordingGatewayHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int PaymentLookupCount { get; private set; }
        public string? LastRequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/payments/", StringComparison.Ordinal))
                PaymentLookupCount++;

            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return responder(request, RequestCount);
        }
    }
}
