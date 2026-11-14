using System.Security.Claims;
using SmartPrint.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrinterEndpoints
{
    public static RouteGroupBuilder MapShopPrinterEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/printers", async (
            ClaimsPrincipal principal,
            ShopPrinterService printers,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(principal, out var tenantId))
            {
                return Results.Forbid();
            }

            var result = await printers.ListAsync(tenantId, ct);
            return Results.Ok(result);
        });

        shop.MapPost("/printers", async (
            CreatePrinterRequest request,
            ClaimsPrincipal principal,
            ShopPrinterService printers,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await printers.CreateAsync(tenantId, request, ct);
            if (result.Failure == ShopPrinterMutationFailure.InvalidPrinter)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["printer"] = ["A name, manufacturer, model and supported connection type are required."]
                });
            }

            if (result.Failure == ShopPrinterMutationFailure.InvalidCapabilities)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["capabilitiesJson"] = ["Printer capabilities must be a valid JSON object under 16 KB."]
                });
            }

            if (result.Failure == ShopPrinterMutationFailure.QuotaExceeded)
            {
                return Results.Conflict(new
                {
                    code = result.QuotaCode,
                    message = result.QuotaMessage,
                    current = result.QuotaCurrent,
                    maximum = result.QuotaMaximum
                });
            }

            var printer = result.Printer!;
            return Results.Created($"/api/v1/shop/printers/{printer.Id}", new
            {
                printer.Id,
                printer.DisplayName,
                printer.Manufacturer,
                printer.Model,
                printer.ConnectionType,
                printer.OutputMode,
                printer.ConnectionAddress,
                printer.QueueName,
                printer.CapabilitiesJson,
                printer.Status,
                printer.IsActive
            });
        });

        shop.MapPatch("/printers/{printerId:guid}/active", async (
            Guid printerId,
            bool isActive,
            ClaimsPrincipal principal,
            ShopPrinterService printers,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await printers.SetActiveAsync(tenantId, printerId, isActive, ct);
            if (result.Failure == ShopPrinterMutationFailure.NotFound)
                return Results.NotFound();

            if (result.Failure == ShopPrinterMutationFailure.ActiveOutput)
            {
                return Results.Conflict(new
                {
                    code = "printer_has_active_output",
                    message = $"Printer cannot be disabled while job {result.ActiveJobNumber} is active in the Windows Agent or spooler. Resolve that same job first; disabling it does not cancel Windows output."
                });
            }

            var printer = result.Printer!;
            return Results.Ok(new { printer.Id, printer.IsActive, printer.UpdatedUtc });
        });

        return shop;
    }
}
