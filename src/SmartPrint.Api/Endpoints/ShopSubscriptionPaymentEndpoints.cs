using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class ShopSubscriptionPaymentEndpoints
{
    public static RouteGroupBuilder MapShopSubscriptionPaymentEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/subscription/plans", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!HasShopAccess(principal))
                return Results.Forbid();

            // Expose only the latest published paid version of each INR plan.
            // A payment always references the immutable version selected here.
            var publishedPlans = await (
                from version in db.SubscriptionPlanVersions.AsNoTracking()
                join plan in db.SubscriptionPlans.AsNoTracking() on version.PlanId equals plan.Id
                where plan.IsActive &&
                      !plan.IsSystemPlan &&
                      version.Status == "Published" &&
                      version.Currency == "INR"
                orderby plan.Name, version.Version descending
                select new
                {
                    id = version.Id,
                    planCode = plan.Code,
                    name = plan.Name,
                    description = plan.Description,
                    version = version.Version,
                    currency = version.Currency,
                    monthlyPriceMinor = version.MonthlyPriceMinor,
                    annualPriceMinor = version.AnnualPriceMinor,
                    maxPrinters = version.MaxPrinters,
                    maxAgentDevices = version.MaxAgentDevices,
                    maxQrStations = version.MaxQrStations,
                    maxMonthlyPrintJobs = version.MaxMonthlyPrintJobs,
                    maxDocumentBytes = version.MaxDocumentBytes,
                    cloudAssistedPrintingEnabled = version.CloudAssistedPrintingEnabled,
                    whiteLabelEnabled = version.WhiteLabelEnabled,
                    advancedReportsEnabled = version.AdvancedReportsEnabled,
                    publishedUtc = version.PublishedUtc
                })
                .ToListAsync(ct);
            var plans = publishedPlans
                .GroupBy(item => item.planCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Where(item => item.monthlyPriceMinor > 0 || item.annualPriceMinor > 0)
                .ToArray();

            return Results.Ok(plans);
        });

        shop.MapGet("/subscription/payments", async (
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!HasShopAccess(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var payments = await db.ShopSubscriptionPayments.AsNoTracking()
                .Where(item => item.TenantId == tenantId)
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.PlanVersionId,
                    item.PlanCode,
                    item.BillingInterval,
                    item.AmountMinor,
                    item.Currency,
                    item.Status,
                    item.RazorpayOrderId,
                    item.RazorpayPaymentId,
                    item.CreatedUtc,
                    item.VerifiedUtc,
                    item.AssignedSubscriptionId
                })
                .ToListAsync(ct);

            return Results.Ok(payments);
        });

        shop.MapPost("/subscription/payments/{paymentId:guid}/resume", async (
            Guid paymentId,
            ClaimsPrincipal principal,
            SubscriptionRazorpayService payments,
            CancellationToken ct) =>
        {
            if (!HasShopAccess(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            try
            {
                var order = await payments.ResumeOrderAsync(tenantId, paymentId, ct);
                return Results.Ok(order);
            }
            catch (SubscriptionPaymentException exception)
            {
                return PaymentProblem(exception);
            }
        });

        shop.MapPost("/subscription/payments/orders", async (
            CreateShopSubscriptionPaymentOrderRequest request,
            ClaimsPrincipal principal,
            SubscriptionRazorpayService payments,
            CancellationToken ct) =>
        {
            if (!HasShopAccess(principal) ||
                !TryGetTenantId(principal, out var tenantId) ||
                !Guid.TryParse(principal.FindFirstValue("sub"), out var actorUserId))
            {
                return Results.Forbid();
            }

            try
            {
                var order = await payments.CreateOrderAsync(
                    tenantId,
                    actorUserId,
                    request.PlanVersionId,
                    request.BillingInterval,
                    request.ClientRequestId,
                    ct);
                return Results.Ok(order);
            }
            catch (SubscriptionPaymentException exception)
            {
                return PaymentProblem(exception);
            }
        });

        shop.MapPost("/subscription/payments/verify", async (
            VerifyShopSubscriptionPaymentRequest request,
            ClaimsPrincipal principal,
            SubscriptionRazorpayService payments,
            CancellationToken ct) =>
        {
            if (!HasShopAccess(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            try
            {
                var result = await payments.VerifyCheckoutAsync(
                    tenantId,
                    request.OrderId,
                    request.PaymentId,
                    request.Signature,
                    ct);
                return Results.Ok(result);
            }
            catch (SubscriptionPaymentException exception)
            {
                return PaymentProblem(exception);
            }
        });

        return shop;
    }

    public static WebApplication MapRazorpaySubscriptionWebhookEndpoint(this WebApplication app)
    {
        app.MapPost("/api/v1/webhooks/razorpay/subscriptions", async (
            HttpRequest request,
            SubscriptionRazorpayService payments,
            CancellationToken ct) =>
        {
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0)
                    break;

                if (body.Length + read > RazorpaySubscriptionPaymentRules.MaximumWebhookBodyBytes)
                    return Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "webhook_body_too_large",
                        detail: "Razorpay webhook body exceeds the allowed size.");

                await body.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            var signature = request.Headers["X-Razorpay-Signature"].FirstOrDefault() ?? string.Empty;
            var eventId = request.Headers["x-razorpay-event-id"].FirstOrDefault() ?? string.Empty;
            try
            {
                var result = await payments.ProcessWebhookAsync(body.ToArray(), signature, eventId, ct);
                return Results.Ok(result);
            }
            catch (SubscriptionPaymentException exception)
            {
                return PaymentProblem(exception);
            }
        }).AllowAnonymous();

        return app;
    }

    private static IResult PaymentProblem(SubscriptionPaymentException exception) =>
        Results.Problem(
            statusCode: exception.StatusCode,
            title: exception.Code,
            detail: exception.Message);

    private static bool HasShopAccess(ClaimsPrincipal principal) =>
        principal.FindAll("shop_role")
            .Any(claim => claim.Value is "ShopOwner" or "ShopManager");

    private static bool TryGetTenantId(ClaimsPrincipal principal, out Guid tenantId) =>
        Guid.TryParse(principal.FindFirstValue("tenant_id"), out tenantId);
}

public sealed record CreateShopSubscriptionPaymentOrderRequest(
    Guid PlanVersionId,
    string BillingInterval,
    string ClientRequestId);

public sealed record VerifyShopSubscriptionPaymentRequest(
    string OrderId,
    string PaymentId,
    string Signature);
