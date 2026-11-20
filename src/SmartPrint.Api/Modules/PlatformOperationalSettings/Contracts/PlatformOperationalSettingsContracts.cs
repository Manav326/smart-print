namespace SmartPrint.Api.Modules.PlatformOperationalSettings.Contracts;

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

public sealed record PlatformOperationalSettingsAuditResponse(
    Guid Id,
    string Reason,
    Guid? ActorUserId,
    DateTimeOffset CreatedUtc,
    string ChangesJson);
