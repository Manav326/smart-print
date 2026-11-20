namespace SmartPrint.Api.Modules.PlatformOperationalSettings.Domain;

public sealed record PlatformOperationalSettingsValues(
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
    string Reason);

/// <summary>
/// Pure validation for operational retention and quota settings.
/// </summary>
public static class PlatformOperationalSettingsRules
{
    public static Dictionary<string, string[]> Validate(PlatformOperationalSettingsValues request)
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
}
