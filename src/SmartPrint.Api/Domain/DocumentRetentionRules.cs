namespace SmartPrint.Api.Domain;

public static class DocumentRetentionRules
{
    public static bool ShouldPurgeCloudDocument(
        string storageMode,
        string jobStatus,
        DateTimeOffset? outputConfirmedUtc,
        DateTimeOffset? outputCompletedUtc,
        DateTimeOffset updatedUtc,
        bool hasFinancialHold,
        DateTimeOffset? purgedUtc,
        DateTimeOffset completedCutoffUtc,
        DateTimeOffset abandonedCutoffUtc)
    {
        if (!string.Equals(storageMode, "CLOUD", StringComparison.OrdinalIgnoreCase) || purgedUtc is not null)
            return false;

        if (string.Equals(jobStatus, "TEST_OUTPUT_READY", StringComparison.OrdinalIgnoreCase))
            return outputCompletedUtc is not null && outputCompletedUtc <= completedCutoffUtc;

        if (string.Equals(jobStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            var completionUtc = outputConfirmedUtc ?? outputCompletedUtc;
            return completionUtc is not null && completionUtc <= completedCutoffUtc;
        }

        var terminalUnpaid = jobStatus is "REJECTED" or "PAYMENT_REJECTED" or "QUOTE_EXPIRED" or "REFUNDED";
        return terminalUnpaid && outputConfirmedUtc is null && outputCompletedUtc is null &&
            !hasFinancialHold && updatedUtc <= abandonedCutoffUtc;
    }
}
