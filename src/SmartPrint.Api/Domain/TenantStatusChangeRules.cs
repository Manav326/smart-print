using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public static class TenantStatusChangeRules
{
    public static bool TryNormalize(
        ChangeTenantStatusRequest request,
        out string nextStatus,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        nextStatus = request.Status?.Trim() ?? string.Empty;
        reason = request.Reason?.Trim() ?? string.Empty;

        return nextStatus is "Active" or "Suspended" or "Inactive" &&
               reason.Length is >= 5 and <= 500;
    }
}
