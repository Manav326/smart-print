namespace SmartPrint.Api.Domain;

public static class PlanEntitlementRules
{
    public static bool CanAdd(long? maximum, long currentCount, long requestedCount = 1) =>
        currentCount >= 0 &&
        requestedCount >= 0 &&
        (maximum is null ||
         (maximum.Value >= 0 &&
          currentCount <= maximum.Value &&
          requestedCount <= maximum.Value - currentCount));

    public static bool IsSubscriptionAccessActive(string? status, DateTimeOffset periodEndUtc, DateTimeOffset now) =>
        IsSubscriptionAccessActive(status, DateTimeOffset.MinValue, periodEndUtc, now);

    public static bool IsSubscriptionAccessActive(
        string? status,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        DateTimeOffset now) =>
        (string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(status, "Trial", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(status, "Scheduled", StringComparison.OrdinalIgnoreCase)) &&
        periodStartUtc <= now &&
        periodEndUtc > now;

    public static string GetEffectiveSubscriptionStatus(
        string? status,
        DateTimeOffset periodEndUtc,
        DateTimeOffset now) =>
        GetEffectiveSubscriptionStatus(status, DateTimeOffset.MinValue, periodEndUtc, now);

    public static string GetEffectiveSubscriptionStatus(
        string? status,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        DateTimeOffset now)
    {
        if (status is null)
        {
            return "Unconfigured";
        }

        var canBecomeActive = string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(status, "Trial", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(status, "Scheduled", StringComparison.OrdinalIgnoreCase);
        if (canBecomeActive && periodEndUtc <= now)
        {
            return "Expired";
        }

        if (canBecomeActive && periodStartUtc > now)
        {
            return "Scheduled";
        }

        if (string.Equals(status, "Scheduled", StringComparison.OrdinalIgnoreCase) &&
            periodStartUtc <= now && periodEndUtc > now)
        {
            return "Active";
        }

        return status;
    }

    public static bool CanFinishAcceptedWorkAfterExpiry(
        string? effectiveSubscriptionStatus,
        DateTimeOffset? periodEndUtc,
        DateTimeOffset acceptedUtc,
        DateTimeOffset now) =>
        string.Equals(effectiveSubscriptionStatus, "Expired", StringComparison.OrdinalIgnoreCase) &&
        periodEndUtc is DateTimeOffset periodEnd &&
        periodEnd <= now &&
        acceptedUtc < periodEnd;

    public static bool AllowsDocument(long? maximumBytes, long requestedBytes) =>
        requestedBytes > 0 &&
        (maximumBytes is null ||
         (maximumBytes.Value > 0 && requestedBytes <= maximumBytes.Value));
}
