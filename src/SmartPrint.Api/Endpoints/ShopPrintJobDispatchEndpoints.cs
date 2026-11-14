using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;
using static SmartPrint.Api.Security.EndpointIdentity;

namespace SmartPrint.Api.Endpoints;

public static class ShopPrintJobDispatchEndpoints
{
    public static RouteGroupBuilder MapShopPrintJobDispatchEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapPost("/print-jobs/{jobId:guid}/retry-pdf-export", async (
            Guid jobId,
            ClaimsPrincipal principal,
            ShopPrintJobDispatchService dispatch,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await dispatch.RetryPdfExportAsync(jobId, tenantId, GetActorId(principal), ct);
            switch (result.Failure)
            {
                case ShopPrintDispatchFailure.NotFound:
                    return Results.NotFound();
                case ShopPrintDispatchFailure.PrivatePrintingOutputRequired:
                    return Results.Conflict(new
                    {
                        code = "private_printing_physical_output_required",
                        message = "Private Printing source documents cannot be exported or retried as managed PDFs."
                    });
                case ShopPrintDispatchFailure.PdfExportNotRecoverable:
                    return Results.Conflict(new
                    {
                        code = "pdf_export_not_recoverable",
                        message = "Only an output-unconfirmed job assigned to a PDF export destination can be retried."
                    });
                case ShopPrintDispatchFailure.PdfExportLocalCopyMissing:
                    return Results.Conflict(new
                    {
                        code = "pdf_export_local_copy_missing",
                        message = "The original protected local document and assigned Agent are required."
                    });
                case ShopPrintDispatchFailure.PdfExportPrerequisitesNotMet:
                    return Results.Conflict(new
                    {
                        code = "pdf_export_prerequisites_not_met",
                        message = "Verified payment and the same paired computer's live PDF export destination are required."
                    });
            }

            var job = result.Job!;
            return Results.Ok(new
            {
                job.Id,
                job.JobNumber,
                job.Status,
                OutputMode = "PDF_EXPORT",
                PrinterName = result.OutputDestinationName
            });
        });

        shop.MapPost("/print-jobs/{jobId:guid}/retry-private-test-output", async (
            Guid jobId,
            ClaimsPrincipal principal,
            ShopPrintJobDispatchService dispatch,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await dispatch.RetryPrivateTestOutputAsync(
                jobId, tenantId, GetActorId(principal), ct);
            if (result.Failure == ShopPrintDispatchFailure.NotFound)
                return Results.NotFound();
            if (result.Failure == ShopPrintDispatchFailure.PrivateTestOutputNotRecoverable)
            {
                return Results.Conflict(new
                {
                    code = "private_test_output_not_recoverable",
                    message = "Only an output-unconfirmed Private Printing test job can be retried while non-production virtual output is enabled."
                });
            }
            if (result.Failure == ShopPrintDispatchFailure.PrivateTestOutputLocalCopyMissing)
            {
                return Results.Conflict(new
                {
                    code = "private_test_local_copy_missing",
                    message = "The original protected local copy and its paired Agent are required. No cloud copy can be used for this retry."
                });
            }
            if (result.Failure == ShopPrintDispatchFailure.PrivateTestOutputPrerequisitesNotMet)
            {
                return Results.Conflict(new
                {
                    code = "private_test_output_prerequisites_not_met",
                    message = "The same paired Agent, its ready virtual test queue and verified payment are required before retrying."
                });
            }

            var job = result.Job!;
            return Results.Ok(new
            {
                job.Id,
                job.JobNumber,
                job.Status,
                OutputMode = "PDF_EXPORT",
                OutputType = PrivatePrintingOutputPolicy.TestVirtual,
                PrinterName = result.OutputDestinationName,
                QueueName = result.OutputQueueName,
                PhysicalOutputConfirmed = false
            });
        });

        shop.MapPost("/print-jobs/{jobId:guid}/confirm-output", async (
            Guid jobId,
            ClaimsPrincipal principal,
            ShopPrintJobDispatchService dispatch,
            CancellationToken ct) =>
        {
            if (!IsShopOwner(principal) || !TryGetTenantId(principal, out var tenantId))
                return Results.Forbid();

            var result = await dispatch.ConfirmOutputAsync(jobId, tenantId, GetActorId(principal), ct);
            if (result.Failure == ShopPrintDispatchFailure.NotFound)
                return Results.NotFound();

            if (result.Failure == ShopPrintDispatchFailure.PdfExportConfirmationNotAllowed)
            {
                var isTestVirtual = result.Job?.OutputType == "TEST_VIRTUAL";
                return Results.Conflict(new
                {
                    code = isTestVirtual ? "test_virtual_output_not_physical" : "pdf_export_not_physical_output",
                    message = isTestVirtual
                        ? "The test PDF was generated locally. It is not a physical print and cannot be marked as physically collected."
                        : "PDF exports are confirmed when the local PDF file is saved and verified. Physical paper-output confirmation does not apply."
                });
            }

            if (result.Failure == ShopPrintDispatchFailure.OutputNotReadyForConfirmation)
            {
                return Results.Conflict(new
                {
                    code = "output_not_ready_for_confirmation",
                    message = "Only a submitted or output-unconfirmed job can be completed after checking the physical pages."
                });
            }

            if (result.Failure == ShopPrintDispatchFailure.PrivateOutputConfirmationInvalid)
            {
                return Results.Conflict(new
                {
                    code = "private_output_confirmation_invalid",
                    message = "Private Printing can be completed only after confirmed physical output from its assigned shop computer."
                });
            }

            var job = result.Job!;
            if (result.DuplicateRequest)
            {
                return Results.Ok(new
                {
                    job.Id,
                    job.Status,
                    job.OutputConfirmedUtc,
                    DuplicateRequest = true
                });
            }

            return Results.Ok(new { job.Id, job.Status, job.OutputConfirmedUtc });
        });

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
