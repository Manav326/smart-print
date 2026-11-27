using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Services;
using SmartPrint.Api.Storage;

namespace SmartPrint.Api.Endpoints;

public static class PrinterDiagnosticEndpoints
{
    private const string TemplateVersion = "SP-DIAG-1";
    private const int MaximumPageBytes = 4 * 1024 * 1024;
    private const int MaximumTotalPageBytes = 8 * 1024 * 1024;

    public static RouteGroupBuilder MapPrinterDiagnosticEndpoints(this RouteGroupBuilder shop)
    {
        shop.MapGet("/printers/{printerId:guid}/diagnostics/profile", async (
            Guid printerId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            if (!TryGetShopActor(principal, out var tenantId, out _) || !IsShopOwner(principal))
                return Results.Forbid();

            var readiness = await GetReadyPrinterAsync(db, environment, tenantId, printerId, ct);
            if (readiness.Failure is not null)
                return readiness.Failure;

            var printer = readiness.Printer!;
            if (!PrinterDiagnosticRules.TryResolveSettings(
                    printer.CapabilitiesJson, out var settings, out var failureReason))
                return Results.Conflict(new { code = "diagnostic_capabilities_unsupported", message = failureReason });

            return Results.Ok(new
            {
                printer.Id,
                printer.DisplayName,
                printer.QueueName,
                printer.ConnectionType,
                printer.OutputMode,
                TemplateVersion,
                settings.PaperSize,
                settings.ColourMode,
                settings.IsDuplex,
                settings.PageCount,
                settings.CapabilityEvidence,
                CapabilitiesObservedUtc = printer.LastSeenUtc
            });
        });

        shop.MapGet("/printers/{printerId:guid}/diagnostics", async (
            Guid printerId,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            int? take,
            CancellationToken ct) =>
        {
            if (!TryGetShopActor(principal, out var tenantId, out _) || !IsShopOwner(principal))
                return Results.Forbid();

            var printerExists = await db.PrinterDevices.AsNoTracking()
                .AnyAsync(item => item.Id == printerId && item.TenantId == tenantId, ct);
            if (!printerExists)
                return Results.NotFound();

            var limit = Math.Clamp(take ?? 10, 1, 25);
            var attempts = await db.PrinterDiagnosticAttempts.AsNoTracking()
                .Where(item => item.TenantId == tenantId && item.PrinterDeviceId == printerId)
                .OrderByDescending(item => item.CreatedUtc)
                .Take(limit)
                .Select(item => new
                {
                    item.Id,
                    item.PrintJobId,
                    item.TemplateVersion,
                    item.OwnerDecision,
                    item.OwnerNotes,
                    item.CreatedUtc,
                    item.ConfirmedUtc,
                    Job = db.PrintJobs.AsNoTracking()
                        .Where(job => job.TenantId == tenantId && job.Id == item.PrintJobId)
                        .Select(job => new
                        {
                            job.Status,
                            job.SpoolerStatus,
                            job.SpoolerSubmittedUtc,
                            job.SpoolerObservedUtc,
                            job.FailureCode,
                            job.OutputType,
                            job.Copies,
                            job.PageCount,
                            job.LocalQueueReceivedUtc,
                            job.PrintDispatchAttempts,
                            job.SpoolerPagesPrinted,
                            job.SpoolerTotalPages,
                            job.OutputCompletedUtc
                        })
                        .SingleOrDefault()
                })
                .ToListAsync(ct);

            return Results.Ok(attempts);
        });

        shop.MapPost("/printers/{printerId:guid}/diagnostics", async (
            Guid printerId,
            PrinterDiagnosticStartRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            DocumentStorage storage,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            if (!TryGetShopActor(principal, out var tenantId, out var actorId) ||
                !IsShopOwner(principal))
                return Results.Forbid();

            var idempotencyKey = request.IdempotencyKey?.Trim();
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 64 ||
                idempotencyKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["idempotencyKey"] = ["Provide a unique 8–64 character idempotency key."]
                });

            if (request.PagesJpegBase64 is null || request.PagesJpegBase64.Count is < 1 or > 2 ||
                request.PagesJpegBase64.Any(page => string.IsNullOrWhiteSpace(page) || page.Length > ((MaximumPageBytes + 2) / 3) * 4 + 8) ||
                request.PagesJpegBase64.Sum(page => (long)page.Length) > ((MaximumTotalPageBytes + 2) / 3) * 4 + 16)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pagesJpegBase64"] = ["Provide one JPEG page, or two when the driver explicitly supports duplex, within the diagnostic size limit."]
                });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var lockedPrinters = await db.PrinterDevices.FromSqlInterpolated(
                $"SELECT * FROM \"PrinterDevices\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {printerId} FOR UPDATE")
                .ToListAsync(ct);
            var printer = lockedPrinters.SingleOrDefault();
            if (printer is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }

            var now = DateTimeOffset.UtcNow;
            var readinessFailure = await ValidatePrinterReadyAsync(
                db, environment, tenantId, printer, now, ct);
            if (readinessFailure is not null)
            {
                await transaction.RollbackAsync(ct);
                return readinessFailure;
            }

            var storedIdempotencyKey = $"PD:{printerId:N}:{idempotencyKey}";
            var duplicate = await (
                from attempt in db.PrinterDiagnosticAttempts.AsNoTracking()
                join job in db.PrintJobs.AsNoTracking()
                    on new { attempt.TenantId, PrintJobId = attempt.PrintJobId }
                    equals new { job.TenantId, PrintJobId = job.Id }
                where attempt.TenantId == tenantId && job.IdempotencyKey == storedIdempotencyKey
                select new { attempt.Id, attempt.PrintJobId, job.JobNumber, job.Status, attempt.CreatedUtc }
            ).SingleOrDefaultAsync(ct);
            if (duplicate is not null)
            {
                await transaction.CommitAsync(ct);
                return Results.Ok(new
                {
                    duplicate.Id,
                    duplicate.PrintJobId,
                    duplicate.JobNumber,
                    duplicate.Status,
                    duplicate.CreatedUtc,
                    DuplicateRequest = true
                });
            }

            var unresolvedAttempt = await db.PrinterDiagnosticAttempts.AsNoTracking()
                .AnyAsync(item => item.TenantId == tenantId &&
                    item.PrinterDeviceId == printerId && item.ConfirmedUtc == null, ct);
            if (unresolvedAttempt)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "printer_diagnostic_already_pending",
                    message = "Confirm the previous diagnostic result before starting another test for this printer."
                });
            }

            if (!PrinterDiagnosticRules.TryResolveSettings(
                    printer.CapabilitiesJson, out var settings, out var failureReason))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "diagnostic_capabilities_unsupported", message = failureReason });
            }

            if (!PrinterDiagnosticRules.MatchesResolvedSettings(
                    settings, request.PaperSize, request.ColourMode, request.IsDuplex,
                    request.PagesJpegBase64.Count) ||
                !string.Equals(request.TemplateVersion, TemplateVersion, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "diagnostic_profile_changed",
                    message = "The printer capability profile or diagnostic template changed. Reload the diagnostic profile and regenerate the test page."
                });
            }

            var pageImages = new List<byte[]>(request.PagesJpegBase64.Count);
            long totalBytes = 0;
            try
            {
                foreach (var encodedPage in request.PagesJpegBase64)
                {
                    var bytes = Convert.FromBase64String(encodedPage);
                    if (bytes.Length is < 100 or > MaximumPageBytes)
                        throw new InvalidDataException("A diagnostic page is empty or exceeds the image size limit.");
                    totalBytes = checked(totalBytes + bytes.Length);
                    if (totalBytes > MaximumTotalPageBytes)
                        throw new InvalidDataException("The diagnostic pages exceed the total image size limit.");
                    pageImages.Add(bytes);
                }
            }
            catch (FormatException)
            {
                await transaction.RollbackAsync(ct);
                return Results.BadRequest(new { code = "diagnostic_image_invalid", message = "The diagnostic page image is not valid base64." });
            }
            catch (InvalidDataException exception)
            {
                await transaction.RollbackAsync(ct);
                return Results.BadRequest(new { code = "diagnostic_image_invalid", message = exception.Message });
            }

            byte[]? pdfBytes = null;
            string? storageKey = null;
            try
            {
                pdfBytes = PrinterDiagnosticPdfBuilder.CreatePdf(pageImages, settings.PaperSize);
                var jobId = Guid.NewGuid();
                var documentId = Guid.NewGuid();
                var attemptId = Guid.NewGuid();
                var jobNumberSource = $"D{now:yyMMddHHmm}-{jobId:N}";
                var jobNumber = jobNumberSource[..Math.Min(25, jobNumberSource.Length)];
                var fileName = $"SmartPrint-Diagnostic-{jobId:N}.pdf";

                await using var pdfStream = new MemoryStream(pdfBytes, writable: false);
                var stored = await storage.ValidateAndStoreStreamingAsync(
                    tenantId, documentId, fileName, pdfStream, pdfBytes.LongLength, ct);
                storageKey = stored.StorageKey;

                var outputType = printer.OutputMode == PrinterTypePolicy.UnsupportedVirtual
                    ? PrivatePrintingOutputPolicy.TestVirtual
                    : PrinterTypePolicy.Physical;

                var job = new PrintJob
                {
                    Id = jobId,
                    TenantId = tenantId,
                    Origin = "PRINTER_DIAGNOSTIC",
                    PrinterDeviceId = printer.Id,
                    PrinterNameSnapshot = printer.DisplayName,
                    PrinterQueueNameSnapshot = printer.QueueName,
                    PrinterSnapshotUtc = now,
                    JobNumber = jobNumber,
                    Status = "DIAGNOSTIC_QUEUED",
                    IdempotencyKey = storedIdempotencyKey,
                    PageCount = stored.Analysis.PageCount,
                    Copies = 1,
                    ColourMode = settings.ColourMode,
                    PaperSize = settings.PaperSize,
                    IsDuplex = settings.IsDuplex,
                    Orientation = "PORTRAIT",
                    PagesPerSheet = 1,
                    PrintKind = "DOCUMENT",
                    PhotoCount = 0,
                    PhotoUnitPriceMinorSnapshot = 0,
                    Currency = "INR",
                    QuotedAmountMinor = 0,
                    OutputType = outputType,
                    SpoolerStatus = "AWAITING_AGENT",
                    CreatedUtc = now,
                    UpdatedUtc = now
                };

                var document = new PrintDocument
                {
                    Id = documentId,
                    TenantId = tenantId,
                    PrintJobId = jobId,
                    OriginalFileName = fileName,
                    MediaType = stored.Analysis.MediaType,
                    FileExtension = stored.Analysis.Extension,
                    SizeBytes = stored.SizeBytes,
                    Sha256 = stored.Sha256,
                    StorageKey = stored.StorageKey,
                    StorageMode = "CLOUD",
                    PageSizesJson = stored.Analysis.PageSizesJson,
                    CreatedUtc = now
                };

                var attempt = new PrinterDiagnosticAttempt
                {
                    Id = attemptId,
                    TenantId = tenantId,
                    PrinterDeviceId = printer.Id,
                    AgentDeviceId = printer.AgentDeviceId!.Value,
                    PrintJobId = jobId,
                    TemplateVersion = TemplateVersion,
                    CapabilitiesJson = string.IsNullOrWhiteSpace(printer.CapabilitiesJson)
                        ? "{}"
                        : printer.CapabilitiesJson,
                    CreatedUtc = now
                };

                db.PrintJobs.Add(job);
                db.PrintDocuments.Add(document);
                db.PrinterDiagnosticAttempts.Add(attempt);
                db.PrintJobEvents.Add(new PrintJobEvent
                {
                    TenantId = tenantId,
                    PrintJobId = jobId,
                    PreviousStatus = "NONE",
                    NewStatus = job.Status,
                    ActorType = "ShopOwner",
                    ActorId = actorId,
                    ReasonCode = "SHOP_OWNER_STARTED_PRINTER_DIAGNOSTIC",
                    CreatedUtc = now
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return Results.Created($"/api/v1/shop/printer-diagnostics/{attempt.Id}", new
                {
                    attempt.Id,
                    attempt.PrintJobId,
                    job.JobNumber,
                    job.Status,
                    job.Copies,
                    job.PageCount,
                    job.PaperSize,
                    job.ColourMode,
                    job.IsDuplex,
                    job.OutputType,
                    attempt.TemplateVersion,
                    attempt.CreatedUtc
                });
            }
            catch
            {
                if (storageKey is not null)
                {
                    try { await storage.DeleteAsync(storageKey); }
                    catch { /* Keep the original error; retention can clean an orphan. */ }
                }
                throw;
            }
            finally
            {
                if (pdfBytes is not null)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(pdfBytes);
                foreach (var page in pageImages)
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(page);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(12 * 1024 * 1024));

        shop.MapPost("/printer-diagnostics/{attemptId:guid}/confirm", async (
            Guid attemptId,
            PrinterDiagnosticConfirmationRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetShopActor(principal, out var tenantId, out var actorId) || !IsShopOwner(principal))
                return Results.Forbid();

            var decision = request.Decision?.Trim().ToUpperInvariant();
            var notes = request.Notes?.Trim();
            if (decision is null || (notes?.Length ?? 0) > 500)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["decision"] = ["Choose a valid diagnostic result and keep notes within 500 characters."]
                });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var attempt = await db.PrinterDiagnosticAttempts.SingleOrDefaultAsync(
                item => item.Id == attemptId && item.TenantId == tenantId, ct);
            if (attempt is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.NotFound();
            }

            var job = await db.PrintJobs.SingleOrDefaultAsync(
                item => item.Id == attempt.PrintJobId && item.TenantId == tenantId, ct);
            if (job is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "diagnostic_execution_record_missing" });
            }

            if (attempt.ConfirmedUtc is not null)
            {
                var sameDecision = string.Equals(attempt.OwnerDecision, decision, StringComparison.Ordinal) &&
                    string.Equals(attempt.OwnerNotes, notes, StringComparison.Ordinal);
                await transaction.RollbackAsync(ct);
                return sameDecision
                    ? Results.Ok(new { attempt.Id, attempt.OwnerDecision, attempt.ConfirmedUtc, DuplicateRequest = true })
                    : Results.Conflict(new { code = "diagnostic_result_already_confirmed", message = "This diagnostic result has already been recorded and cannot be overwritten." });
            }

            var virtualOutput = string.Equals(job.OutputType, PrivatePrintingOutputPolicy.TestVirtual, StringComparison.Ordinal);
            var allowed = virtualOutput
                ? decision is "OUTPUT_RECEIVED_CORRECTLY" or "OUTPUT_RECEIVED_WITH_ISSUE" or "NO_OUTPUT_RECEIVED"
                : decision is "PRINTED_CORRECTLY" or "PRINTED_WITH_ISSUE" or "NOTHING_PRINTED";
            if (!allowed)
            {
                await transaction.RollbackAsync(ct);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["decision"] = [virtualOutput
                        ? "Choose Output received correctly, Output received with issue, or No output received."
                        : "Choose Printed correctly, Printed with issue, or Nothing printed."]
                });
            }

            if (job.LocalQueueReceivedUtc is null || job.PrintDispatchAttempts < 1)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "diagnostic_not_yet_attempted", message = "The Agent has not yet attempted this diagnostic. Keep the shop computer connected and wait for the print result before confirming." });
            }

            var now = DateTimeOffset.UtcNow;
            var terminalOutput = PrinterDiagnosticRules.IsTerminalOutputObservation(
                job.OutputType,
                job.Status,
                job.SpoolerStatus,
                job.SpoolerSubmittedUtc,
                job.SpoolerObservedUtc,
                job.FailureCode,
                job.SpoolerJobId,
                job.SpoolerPagesPrinted,
                job.SpoolerTotalPages,
                job.OutputCompletedUtc,
                now);
            if (!terminalOutput)
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    code = "diagnostic_output_not_terminal",
                    message = "Windows has not finished this print attempt yet. SmartPrint will ask for your confirmation only after it reports PRINTED, verifies the spooler job is no longer present, or confirms that submission was blocked before StartDoc."
                });
            }

            if (decision is "PRINTED_CORRECTLY" && !virtualOutput &&
                !string.Equals(job.SpoolerStatus, "PRINTED", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "diagnostic_not_verified_printed", message = "The Windows Agent has not reported a completed PRINTED observation for this physical test." });
            }

            if (decision is "PRINTED_WITH_ISSUE" &&
                !string.Equals(job.SpoolerStatus, "PRINTED", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(job.SpoolerStatus, "OUTPUT_UNCONFIRMED", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "diagnostic_output_not_observed", message = "There is no terminal spooler output observation to attach this issue to." });
            }

            if (decision is "OUTPUT_RECEIVED_CORRECTLY" &&
                job.Status != "TEST_OUTPUT_READY")
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "diagnostic_virtual_output_not_ready", message = "The virtual output artifact has not been confirmed ready yet." });
            }

            if (decision is "OUTPUT_RECEIVED_WITH_ISSUE" &&
                job.Status is not ("TEST_OUTPUT_READY" or "OUTPUT_UNCONFIRMED"))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { code = "diagnostic_virtual_output_not_observed", message = "No virtual output artifact or verified spooler-absence observation is available to attach this issue to." });
            }

            var actorExists = await db.Users.AnyAsync(
                user => user.Id == actorId && user.TenantId == tenantId, ct);
            if (!actorExists)
            {
                await transaction.RollbackAsync(ct);
                return Results.Forbid();
            }

            attempt.OwnerDecision = decision;
            attempt.OwnerNotes = string.IsNullOrWhiteSpace(notes) ? null : notes;
            attempt.ConfirmedByUserId = actorId;
            attempt.ConfirmedUtc = now;

            var nextStatus = decision switch
            {
                "PRINTED_CORRECTLY" => "DIAGNOSTIC_VERIFIED",
                "OUTPUT_RECEIVED_CORRECTLY" => "DIAGNOSTIC_RECEIVED",
                "PRINTED_WITH_ISSUE" or "OUTPUT_RECEIVED_WITH_ISSUE" => "DIAGNOSTIC_ISSUE",
                _ => "DIAGNOSTIC_NO_OUTPUT"
            };
            var previousStatus = job.Status;
            job.Status = nextStatus;
            job.UpdatedUtc = now;
            job.PrintDispatchLeaseExpiresUtc = null;
            // Retain the diagnostic PDF only for the configured completed-document period.
            job.OutputCompletedUtc = now;
            if (decision == "PRINTED_CORRECTLY")
                job.OutputConfirmedUtc = now;

            db.PrintJobEvents.Add(new PrintJobEvent
            {
                TenantId = tenantId,
                PrintJobId = job.Id,
                PreviousStatus = previousStatus,
                NewStatus = nextStatus,
                ActorType = "ShopOwner",
                ActorId = actorId,
                ReasonCode = "SHOP_OWNER_CONFIRMED_PRINTER_DIAGNOSTIC_" + decision,
                CreatedUtc = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Ok(new
            {
                attempt.Id,
                attempt.OwnerDecision,
                attempt.OwnerNotes,
                attempt.ConfirmedUtc,
                JobStatus = job.Status,
                PhysicalOutputConfirmed = decision == "PRINTED_CORRECTLY"
            });
        });

        return shop;
    }

    private static async Task<(PrinterDevice? Printer, IResult? Failure)> GetReadyPrinterAsync(
        SmartPrintDbContext db,
        IHostEnvironment environment,
        Guid tenantId,
        Guid printerId,
        CancellationToken ct)
    {
        var printer = await db.PrinterDevices.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == printerId && item.TenantId == tenantId, ct);
        if (printer is null)
            return (null, Results.NotFound());

        var failure = await ValidatePrinterReadyAsync(db, environment, tenantId, printer, DateTimeOffset.UtcNow, ct);
        return failure is null ? (printer, null) : (null, failure);
    }

    private static async Task<IResult?> ValidatePrinterReadyAsync(
        SmartPrintDbContext db,
        IHostEnvironment environment,
        Guid tenantId,
        PrinterDevice printer,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!printer.IsActive || printer.AgentDeviceId is not Guid agentId ||
            string.IsNullOrWhiteSpace(printer.QueueName) ||
            printer.LastSeenUtc is null || printer.LastSeenUtc < now - TimeSpan.FromMinutes(2) ||
            !PrinterTypePolicy.IsWorkingWindowsQueue(printer.Status))
            return Results.Conflict(new
            {
                code = "diagnostic_printer_not_ready",
                message = "The printer must be selected, connected to its paired Windows Agent, and recently reported Ready or Busy before a diagnostic can start."
            });

        var physical = string.Equals(printer.OutputMode, PrinterTypePolicy.Physical, StringComparison.OrdinalIgnoreCase);
        var isVirtual = string.Equals(printer.OutputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase);
        if (!physical && !isVirtual)
            return Results.Conflict(new { code = "diagnostic_output_mode_unsupported", message = "Diagnostics are available only for installed physical Windows queues and explicitly authorised non-production virtual test queues." });
        if (isVirtual && environment.IsProduction())
            return Results.Conflict(new { code = "virtual_diagnostic_disabled_in_production", message = "Virtual-printer diagnostics are disabled in production." });

        var agentOnline = await db.AgentDevices.AsNoTracking().AnyAsync(agent =>
            agent.Id == agentId && agent.TenantId == tenantId &&
            agent.RevokedUtc == null && agent.Status == "Online" &&
            agent.LastSeenUtc != null && agent.LastSeenUtc >= now - TimeSpan.FromMinutes(2), ct);
        if (!agentOnline)
            return Results.Conflict(new { code = "diagnostic_agent_offline", message = "Reconnect the paired Windows Print Agent before starting a diagnostic." });

        if (isVirtual)
        {
            var grantActive = await db.VirtualPrinterTestGrants.AsNoTracking().AnyAsync(grant =>
                grant.TenantId == tenantId && grant.PrinterDeviceId == printer.Id &&
                grant.AgentDeviceId == agentId &&
                grant.IdentityFingerprint == printer.IdentityFingerprint &&
                grant.RevokedUtc == null && grant.CreatedUtc <= now && grant.ExpiresUtc > now, ct);
            if (!grantActive)
                return Results.Conflict(new { code = "virtual_diagnostic_grant_required", message = "This virtual queue needs a current platform-approved test grant for this exact shop, Agent and queue identity." });
        }

        return null;
    }

    private static bool TryGetShopActor(ClaimsPrincipal principal, out Guid tenantId, out Guid actorId)
    {
        var tenant = principal.FindFirstValue("tenant_id");
        var actor = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
            principal.FindFirstValue(ClaimTypes.NameIdentifier);
        tenantId = Guid.Empty;
        actorId = Guid.Empty;
        return Guid.TryParse(tenant, out tenantId) && Guid.TryParse(actor, out actorId);
    }

    private static bool IsShopOwner(ClaimsPrincipal principal) =>
        principal.FindAll("shop_role").Any(claim => claim.Value is "ShopOwner" or "ShopManager");
}

public sealed record PrinterDiagnosticStartRequest(
    string TemplateVersion,
    string PaperSize,
    string ColourMode,
    bool IsDuplex,
    string IdempotencyKey,
    IReadOnlyList<string> PagesJpegBase64);

public sealed record PrinterDiagnosticConfirmationRequest(string Decision, string? Notes);
