namespace SmartPrint.Api.Domain;

public static class TenantAccessRules
{
    public static bool IsActive(string? status) =>
        string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase);

    public static bool IsReadRecoveryOperation(string? method, string? path, bool shopWorkspace)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!shopWorkspace) return true;

        var normalizedPath = (path ?? string.Empty).TrimEnd('/');
        return !normalizedPath.EndsWith("/profile", StringComparison.OrdinalIgnoreCase);
    }
}