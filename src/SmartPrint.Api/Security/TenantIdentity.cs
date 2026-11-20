using System.Security.Claims;

namespace SmartPrint.Api.Security;

/// <summary>
/// Resolves the tenant boundary from a verified SmartPrint principal.
/// Callers must not replace this with a tenant id supplied in a request body or query.
/// </summary>
public static class TenantIdentity
{
    public static bool TryGetTenantId(ClaimsPrincipal principal, out Guid tenantId) =>
        Guid.TryParse(principal.FindFirstValue("tenant_id"), out tenantId);
}
