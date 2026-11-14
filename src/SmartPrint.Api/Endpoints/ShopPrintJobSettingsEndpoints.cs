using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobSettingsEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobSettingsEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPatch("/print-jobs/{jobId:guid}/settings", async (
            Guid jobId,
            UpdatePrintJobSettingsRequest request,
            ClaimsPrincipal principal,
            ShopPrintJobSettingsService settingsService,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            if (!PrintJobSettingsRules.TryNormalize(
                    request.ColourMode,
                    request.PaperSize,
                    request.IsDuplex,
                    request.Copies,
                    request.Orientation,
                    out var settings))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printOptions"] = ["Choose supported paper, orientation, colour, sides and 1–100 copies."]
                });
            }

            var result = await settingsService.ReviseAsync(
                tenantId,
                GetActorId(principal),
                jobId,
                settings,
                ct);

            if (result.Failure == ShopPrintJobSettingsFailure.NotFound)
            {
                return Results.NotFound();
            }

            if (result.Failure == ShopPrintJobSettingsFailure.Locked)
            {
                return Results.Conflict(new
                {
                    code = "print_settings_locked",
                    message = "Print options can only be adjusted before payment begins and before the job is handed to the Agent."
                });
            }

            if (result.Failure == ShopPrintJobSettingsFailure.PaymentAlreadyStarted)
            {
                return Results.Conflict(new
                {
                    code = "payment_already_started",
                    message = "This job or its batch already has a payment attempt. Do not change its quoted settings; resolve the payment or refund first."
                });
            }

            if (result.Failure == ShopPrintJobSettingsFailure.PricingNotConfigured)
            {
                return Results.Conflict(new
                {
                    code = "shop_pricing_not_configured",
                    message = "Configure shop print prices before revising this quote."
                });
            }

            if (result.Failure == ShopPrintJobSettingsFailure.DuplexPricingNotConfigured)
            {
                return Results.Conflict(new
                {
                    code = "duplex_pricing_not_configured",
                    message = result.Message ?? "Duplex printing is not currently priced by this shop."
                });
            }

            var job = result.Job!;
            return Results.Ok(new
            {
                job.Id,
                job.JobNumber,
                job.Status,
                job.PageCount,
                job.Copies,
                job.ColourMode,
                job.PaperSize,
                job.IsDuplex,
                job.Orientation,
                job.Currency,
                job.QuotedAmountMinor,
                AmountMajor = job.QuotedAmountMinor / 100m,
                job.PricingVersion,
                job.QuoteExpiresUtc,
                CustomerApprovalRequired = true
            });
        });

        return shop;
    }
}
