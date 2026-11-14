using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobDispatchEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobDispatchEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPost("/print-jobs/{jobId:guid}/dispatch", async (
            Guid jobId,
            ClaimsPrincipal principal,
            ShopPrintJobDispatchService dispatch,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await dispatch.DispatchAsync(jobId, tenantId, GetActorId(principal), ct);
            switch (result.Failure)
            {
                case ShopPrintDispatchFailure.NotFound:
                    return Results.NotFound();
                case ShopPrintDispatchFailure.JobNotReady:
                    return Results.Conflict(new
                    {
                        code = "job_not_ready_for_print_dispatch",
                        message = "The paid document must be secured by its assigned agent before printing can start."
                    });
                case ShopPrintDispatchFailure.BatchPrinterMismatch:
                    return Results.Conflict(new
                    {
                        code = "batch_printer_mismatch",
                        message = "Every accepted file in this batch must use the same printer. Assign one printer to the whole batch before starting output."
                    });
                case ShopPrintDispatchFailure.PrerequisitesNotMet:
                    return Results.Conflict(new
                    {
                        code = "print_prerequisites_not_met",
                        message = "Verified payment and the same paired computer's live printer queue are required."
                    });
                case ShopPrintDispatchFailure.PrivatePrintingOutputRequired:
                    return Results.Conflict(new
                    {
                        code = "private_printing_physical_output_required",
                        message = "Private Printing requires a connected physical printer. Virtual printers are available only when the Company Main Admin enables non-production test output."
                    });
            }

            var job = result.Job!;
            if (result.DuplicateRequest)
                return Results.Ok(new { job.Id, job.Status, job.SpoolerJobName, DuplicateRequest = true });

            var printer = result.Printer!;
            return Results.Ok(new
            {
                job.Id,
                job.JobNumber,
                job.Status,
                PrinterName = printer.DisplayName,
                printer.QueueName,
                printer.OutputMode,
                printer.AgentId,
                job.SpoolerJobName
            });
        });

        return shop;
    }
}
