using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.PlatformOperationalSettings.Contracts;
using SmartPrint.Api.Modules.PlatformOperationalSettings.Domain;
using OperationalSettingsEntity = SmartPrint.Api.Domain.PlatformOperationalSettings;

namespace SmartPrint.Api.Modules.PlatformOperationalSettings.Services;

/// <summary>
/// Owns platform operational settings reads, row-locked updates and audit persistence.
/// </summary>
public sealed class PlatformOperationalSettingsService(SmartPrintDbContext db)
{
    public async Task<PlatformOperationalSettingsResponse?> GetAsync(CancellationToken ct)
    {
        var settings = await db.PlatformOperationalSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == PlatformOperationalSettings.SingletonIdValue, ct);
        return settings is null ? null : ToResponse(settings);
    }

    public async Task<IReadOnlyList<PlatformOperationalSettingsAuditResponse>> GetHistoryAsync(CancellationToken ct) =>
        await db.PlatformOperationalSettingsAudits.AsNoTracking()
            .OrderByDescending(item => item.CreatedUtc)
            .Take(100)
            .Select(item => new PlatformOperationalSettingsAuditResponse(
                item.Id, item.Reason, item.ActorUserId, item.CreatedUtc, item.ChangesJson))
            .ToListAsync(ct);

    public async Task<PlatformOperationalSettingsUpdateResult> UpdateAsync(
        PlatformOperationalSettingsRequest request,
        Guid actorId,
        CancellationToken ct)
    {
        var errors = PlatformOperationalSettingsRules.Validate(new PlatformOperationalSettingsValues(
            request.TrialMaxMonthlyPrintJobs,
            request.CloudCompletedDocumentRetentionDays,
            request.AgentCompletedDocumentRetentionDays,
            request.AbandonedDocumentRetentionDays,
            request.PendingLocalUploadRetentionDays,
            request.ExpiredUploadTicketRetentionDays,
            request.EmptyCustomerSessionRetentionDays,
            request.StaleRefreshTokenRetentionDays,
            request.ApiRetentionSweepHours,
            request.AgentRetentionSweepHours,
            request.Reason));
        if (errors.Count != 0)
            return new PlatformOperationalSettingsUpdateResult(errors, false, null);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.PlatformOperationalSettings.FromSqlRaw(
            "SELECT * FROM \"PlatformOperationalSettings\" WHERE \"Id\" = 1 FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (locked is null)
        {
            await transaction.RollbackAsync(ct);
            return new PlatformOperationalSettingsUpdateResult(errors, true, null);
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
        return new PlatformOperationalSettingsUpdateResult(errors, false, after);
    }

    private static PlatformOperationalSettingsResponse ToResponse(OperationalSettingsEntity settings) => new(
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

public sealed record PlatformOperationalSettingsUpdateResult(
    IReadOnlyDictionary<string, string[]> ValidationErrors,
    bool SettingsNotInitialized,
    PlatformOperationalSettingsResponse? Response);
