using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobAssignmentEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobAssignmentEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPatch("/print-jobs/{jobId:guid}/printer", async (
            Guid jobId,
            AssignPrintJobPrinterRequest request,
            ClaimsPrincipal principal,
            ShopPrintJobAssignmentService assignment,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await assignment.AssignAsync(
                tenantId,
                GetActorId(principal),
                jobId,
                request.PrinterDeviceId,
                ct);

            if (result.Failure == ShopPrintJobAssignmentFailure.NotFound)
                return Results.NotFound();

            if (result.Failure == ShopPrintJobAssignmentFailure.JobNotAssignable)
            {
                return Results.Conflict(new
                {
                    code = "job_not_assignable",
                    message = "A printer can be assigned after payment is verified and before print output starts. Once printing has started, the whole batch stays on that printer."
                });
            }

            if (result.Failure == ShopPrintJobAssignmentFailure.PrinterNotAvailable)
            {
                return Results.Conflict(new
                {
                    code = "printer_not_available",
                    message = "Select a printer on a paired, online computer with a recent heartbeat and a ready or busy Windows queue."
                });
            }

            if (result.Failure == ShopPrintJobAssignmentFailure.PrivatePrintingOutputRequired)
            {
                return Results.Conflict(new
                {
                    code = "private_printing_physical_output_required",
                    message = "Private Printing requires a connected physical printer. Virtual printers are available only when the Company Main Admin enables non-production test output."
                });
            }

            if (result.Failure == ShopPrintJobAssignmentFailure.Conflict)
            {
                var code = result.ConflictCode ?? "assignment_conflict";
                var message = code switch
                {
                    "local_document_agent_locked" =>
                        "A file in this batch is stored on a different shop computer. All files must stay on that same computer; upload the batch again to move it.",
                    "batch_printer_locked" =>
                        "A file in this batch has already started printing. The whole batch is locked to that printer.",
                    _ => "This batch cannot be assigned to the selected printer. Refresh the queue and try again."
                };

                return Results.Conflict(new { code, message });
            }

            return Results.Ok(new
            {
                Id = result.JobId,
                result.BatchId,
                Status = result.Status,
                PrinterDeviceId = result.PrinterDeviceId,
                PrinterName = result.PrinterName,
                QueueName = result.QueueName,
                AgentDeviceId = result.AgentDeviceId,
                AssignedJobCount = result.AssignedJobCount,
                UpdatedUtc = result.UpdatedUtc
            });
        });

        return shop;
    }
}
