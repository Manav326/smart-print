using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Endpoints;

public static class PlatformOperationalSettingsEndpoints
{
    public static RouteGroupBuilder MapPlatformOperationalSettingsEndpoints(this RouteGroupBuilder platform)
    {
        platform.MapGet("/settings/operations", async (SmartPrintDbContext db, CancellationToken ct) =>
        {
            var settings = await db.PlatformOperationalSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, ct);
            return settings is null ? Results.Problem(statusCode: 503, title: "Platform operational settings are not initialized.") : Results.Ok(ToResponse(settings));
        });

        platform.MapGet("/settings/operations/history", async (SmartPrintDbContext db, CancellationToken ct) =>
        {
            var history = await db.PlatformOperationalSettingsAudits.AsNoTracking()
                .OrderByDescending(item => item.CreatedUtc)
                .Take(100)
                .Select(item => new { item.Id, item.Reason, item.ActorUserId, item.CreatedUtc, item.ChangesJson })
                .ToListAsync(ct);
            return Results.Ok(history);
        });

        platform.MapPut("/settings/operations", async (
            PlatformOperationalSettingsRequest request,
            ClaimsPrincipal principal,
            SmartPrintDbContext db,
            CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);
            var actorValue = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
                principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(actorValue, out var actorId)) return Results.Forbid();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var locked = await db.PlatformOperationalSettings.FromSqlRaw(
                "SELECT * FROM \"PlatformOperationalSettings\" WHERE \"Id\" = 1 FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (locked is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Problem(statusCode: 503, title: "Platform operational settings are not initialized.");
            }

            var before = ToResponse(locked);
            locked.TrialMaxMonthlyPrintJobs = request.TrialMaxMonthlyPrintJobs;
            locked.CloudCompletedDocumentRetentionDays = request.CloudCompletedDocumentRetentionDays;
            locked.AgentCompletedDocumentRetentionDays = request.AgentCompletedDocumentRetentionDays;
            locked.AbandonedDocumentRetentionDays = request.AbandonedDocumentRetentionDays;
            locked.PendingLocalUploadRetentionDays = request.PendingLocalUploadRetentionDays;
            locked.ExpiredUploadTicketRetentionDays = request.ExpiredUploadTicketRetentionDays;
            locked.EmptyCustomerSessionRetentionDays = request.EmptyCustomerSessionRetentionDays;
            locked.StaleRefreshTokenRetentionDays = request.StaleRefreshTokenRetentionDays;
            locked.ApiRetentionSweepHours = request.ApiRetentionSweepHours;
            locked.AgentRetentionSweepHours = request.AgentRetentionSweepHours;
            locked.AllowPaidJobClaimsWhenSuspended = request.AllowPaidJobClaimsWhenSuspended;
            locked.AllowPrivatePrintingVirtualOutput = request.AllowPrivatePrintingVirtualOutput;
            locked.UpdatedByUserId = actorId;
            locked.UpdatedUtc = DateTimeOffset.UtcNow;

            var after = ToResponse(locked);
            db.PlatformOperationalSettingsAudits.Add(new PlatformOperationalSettingsAudit
            {
                ActorUserId = actorId,
                Reason = request.Reason.Trim(),
                ChangesJson = JsonSerializer.Serialize(new { Before = before, After = after }),
                CreatedUtc = locked.UpdatedUtc
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(after);
        });

        return platform;
    }

    private static Dictionary<string, string[]> Validate(PlatformOperationalSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.TrialMaxMonthlyPrintJobs is < 1 or > 10_000_000)
            errors["trialMaxMonthlyPrintJobs"] = ["Use a whole-number quota between 1 and 10,000,000 jobs."];
        ValidateDays(errors, nameof(request.CloudCompletedDocumentRetentionDays), request.CloudCompletedDocumentRetentionDays);
        ValidateDays(errors, nameof(request.AgentCompletedDocumentRetentionDays), request.AgentCompletedDocumentRetentionDays);
        ValidateDays(errors, nameof(request.AbandonedDocumentRetentionDays), request.AbandonedDocumentRetentionDays);
        ValidateDays(errors, nameof(request.PendingLocalUploadRetentionDays), request.PendingLocalUploadRetentionDays);
        ValidateDays(errors, nameof(request.ExpiredUploadTicketRetentionDays), request.ExpiredUploadTicketRetentionDays);
        ValidateDays(errors, nameof(request.EmptyCustomerSessionRetentionDays), request.EmptyCustomerSessionRetentionDays);
        ValidateDays(errors, nameof(request.StaleRefreshTokenRetentionDays), request.StaleRefreshTokenRetentionDays);
        if (request.ApiRetentionSweepHours is < 1 or > 168)
            errors["apiRetentionSweepHours"] = ["The API retention sweep interval must be between 1 and 168 hours."];
        if (request.AgentRetentionSweepHours is < 1 or > 24)
            errors["agentRetentionSweepHours"] = ["The Agent retention sweep interval must be between 1 and 24 hours."];
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 5 or > 500)
            errors["reason"] = ["Provide an audit reason between 5 and 500 characters."];
        return errors;
    }

    private static void ValidateDays(Dictionary<string, string[]> errors, string name, int value)
    {
        if (value is < 1 or > 3650)
            errors[char.ToLowerInvariant(name[0]) + name[1..]] = ["Retention must be between 1 and 3,650 days."];
    }

    private static PlatformOperationalSettingsResponse ToResponse(PlatformOperationalSettings settings) => new(
        settings.TrialMaxMonthlyPrintJobs,
        settings.CloudCompletedDocumentRetentionDays,
        settings.AgentCompletedDocumentRetentionDays,
        settings.AbandonedDocumentRetentionDays,
        settings.PendingLocalUploadRetentionDays,
        settings.ExpiredUploadTicketRetentionDays,
        settings.EmptyCustomerSessionRetentionDays,
        settings.StaleRefreshTokenRetentionDays,
        settings.ApiRetentionSweepHours,
        settings.AgentRetentionSweepHours,
        settings.AllowPaidJobClaimsWhenSuspended,
        settings.AllowPrivatePrintingVirtualOutput,
        settings.UpdatedByUserId,
        settings.UpdatedUtc);
}

public sealed record PlatformOperationalSettingsRequest(
    int TrialMaxMonthlyPrintJobs,
    int CloudCompletedDocumentRetentionDays,
    int AgentCompletedDocumentRetentionDays,
    int AbandonedDocumentRetentionDays,
    int PendingLocalUploadRetentionDays,
    int ExpiredUploadTicketRetentionDays,
    int EmptyCustomerSessionRetentionDays,
    int StaleRefreshTokenRetentionDays,
    int ApiRetentionSweepHours,
    int AgentRetentionSweepHours,
    bool AllowPaidJobClaimsWhenSuspended,
    bool AllowPrivatePrintingVirtualOutput,
    string Reason);

public sealed record PlatformOperationalSettingsResponse(
    int TrialMaxMonthlyPrintJobs,
    int CloudCompletedDocumentRetentionDays,
    int AgentCompletedDocumentRetentionDays,
    int AbandonedDocumentRetentionDays,
    int PendingLocalUploadRetentionDays,
    int ExpiredUploadTicketRetentionDays,
    int EmptyCustomerSessionRetentionDays,
    int StaleRefreshTokenRetentionDays,
    int ApiRetentionSweepHours,
    int AgentRetentionSweepHours,
    bool AllowPaidJobClaimsWhenSuspended,
    bool AllowPrivatePrintingVirtualOutput,
    Guid? UpdatedByUserId,
    DateTimeOffset UpdatedUtc);
