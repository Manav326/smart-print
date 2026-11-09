using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class ShopSubscriptionPaymentEndpoints
{
    public static RouteGroupBuilder MapShopSubscriptionPaymentEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/subscription/plans", async (
            ClaimsPrincipal principal,
            RazorpaySubscriptionPaymentService payments,
            CancellationToken ct) =>
        {
            if (!TryGetShopTenant(principal, out _))
                return Results.Forbid();

            var plans = await payments.GetAvailablePlansAsync(ct);
            return Results.Ok(new
            {
                checkoutAvailable = payments.IsCheckoutConfigured,
                plans
            });
        });

        shop.MapPost("/subscription/payments/orders", async (
            CreateShopSubscriptionOrderRequest request,
            ClaimsPrincipal principal,
            RazorpaySubscriptionPaymentService payments,
            CancellationToken ct) =>
        {
            if (!TryGetShopTenant(principal, out var tenantId))
                return Results.Forbid();

            try
            {
                return Results.Ok(await payments.CreateOrderAsync(tenantId, request, ct));
            }
            catch (RazorpayPaymentException exception)
            {
                return Error(exception);
            }
        });

        shop.MapPost("/subscription/payments/verify", async (
            VerifyShopSubscriptionPaymentRequest request,
            ClaimsPrincipal principal,
            RazorpaySubscriptionPaymentService payments,
            CancellationToken ct) =>
        {
            if (!TryGetShopTenant(principal, out var tenantId))
                return Results.Forbid();

            try
            {
                return Results.Ok(await payments.VerifyCheckoutAsync(tenantId, request, ct));
            }
            catch (RazorpayPaymentException exception)
            {
                return Error(exception);
            }
        });

        shop.MapGet("/subscription/payments/{paymentId:guid}", async (
            Guid paymentId,
            ClaimsPrincipal principal,
            RazorpaySubscriptionPaymentService payments,
            CancellationToken ct) =>
        {
            if (!TryGetShopTenant(principal, out var tenantId))
                return Results.Forbid();

            var status = await payments.GetPaymentStatusAsync(tenantId, paymentId, ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        });

        return shop;
    }

    public static IEndpointRouteBuilder MapRazorpaySubscriptionWebhook(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/webhooks/razorpay/subscriptions", async (
            HttpRequest request,
            RazorpaySubscriptionPaymentService payments,
            CancellationToken ct) =>
        {
            if (request.ContentLength is > 65_536)
            {
                return Results.BadRequest(new { code = "webhook_payload_too_large" });
            }

            using var reader = new StreamReader(
                request.Body, System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync(ct);
            if (rawBody.Length is 0 or > 65_536)
            {
                return Results.BadRequest(new { code = "invalid_webhook_payload_size" });
            }

            var signature = request.Headers["X-Razorpay-Signature"].FirstOrDefault() ?? string.Empty;
            var eventId = request.Headers["X-Razorpay-Event-Id"].FirstOrDefault() ?? string.Empty;
            try
            {
                var result = await payments.ProcessWebhookAsync(rawBody, signature, eventId, ct);
                return Results.Ok(new { result.Duplicate, result.Status });
            }
            catch (RazorpayPaymentException exception)
            {
                return Error(exception);
            }
        }).AllowAnonymous();

        return endpoints;
    }

    private static bool TryGetShopTenant(ClaimsPrincipal principal, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        return principal.FindAll("shop_role").Any(claim =>
                   string.Equals(claim.Value, "ShopOwner", StringComparison.Ordinal)) &&
               Guid.TryParse(principal.FindFirstValue("tenant_id"), out tenantId);
    }

    private static IResult Error(RazorpayPaymentException exception) =>
        Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode);
}
