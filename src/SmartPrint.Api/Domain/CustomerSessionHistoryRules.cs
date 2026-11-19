namespace SmartPrint.Api.Domain;

public static class CustomerSessionHistoryRules
{
    public static readonly TimeSpan ReadOnlyStatusGracePeriod = TimeSpan.FromDays(7);

    public static bool CanReadStatus(
        string? status,
        DateTimeOffset? revokedUtc,
        DateTimeOffset expiresUtc,
        DateTimeOffset now)
    {
        return string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase) &&
               revokedUtc is null &&
               expiresUtc >= now - ReadOnlyStatusGracePeriod;
    }
}
