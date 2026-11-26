using System.Security.Cryptography;
using System.Text;

namespace SmartPrint.Api.Domain;

/// <summary>
/// Identity binding and lifecycle rules for administrator-approved Windows
/// virtual-printer test output. A grant is never a printer classification.
/// </summary>
public static class VirtualPrinterTestGrantPolicy
{
    public static readonly TimeSpan MaximumGrantLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan MinimumGrantLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumInventoryAge = TimeSpan.FromMinutes(2);

    public static string ComputeIdentityFingerprint(
        Guid agentDeviceId,
        string? queueName,
        string? driverName,
        string? portName)
    {
        var canonical = string.Join("\n",
            agentDeviceId.ToString("N"),
            (queueName ?? string.Empty).Trim().ToUpperInvariant(),
            (driverName ?? string.Empty).Trim().ToUpperInvariant(),
            NormalizePortName(portName));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string NormalizePortName(string? portName)
    {
        var value = portName?.Trim() ?? string.Empty;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "ipp" or "ipps")
        {
            // Scheme and host are case-insensitive, but IPP URL paths and
            // query values may be case-sensitive. Preserve those components
            // so a different endpoint cannot inherit an existing grant.
            var authority = uri.IdnHost.ToLowerInvariant();
            if (!uri.IsDefaultPort)
                authority += ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return uri.Scheme.ToLowerInvariant() + "://" + authority + uri.PathAndQuery;
        }

        // Windows queue port identifiers are case-insensitive.
        return value.ToUpperInvariant();
    }

    public static bool IsGrantActive(
        Guid tenantId,
        Guid printerTenantId,
        Guid grantTenantId,
        Guid? printerAgentDeviceId,
        Guid grantAgentDeviceId,
        string? printerIdentityFingerprint,
        string grantIdentityFingerprint,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc,
        DateTimeOffset? revokedUtc,
        DateTimeOffset now) =>
        tenantId == printerTenantId &&
        tenantId == grantTenantId &&
        printerAgentDeviceId == grantAgentDeviceId &&
        !string.IsNullOrWhiteSpace(printerIdentityFingerprint) &&
        string.Equals(printerIdentityFingerprint, grantIdentityFingerprint, StringComparison.Ordinal) &&
        revokedUtc is null &&
        createdUtc <= now &&
        expiresUtc > now &&
        expiresUtc > createdUtc;

    public static bool IsEligibleForSelection(
        string? outputMode,
        string? status,
        DateTimeOffset? printerLastSeenUtc,
        bool grantActive,
        DateTimeOffset now,
        bool isProduction)
    {
        if (isProduction ||
            !string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase) ||
            !grantActive ||
            printerLastSeenUtc is null ||
            printerLastSeenUtc < now - MaximumInventoryAge)
            return false;

        return PrinterTypePolicy.IsWorkingWindowsQueue(status);
    }

    public static bool CanAuthorizeSpoolerSubmission(
        string? outputMode,
        string? outputType,
        bool grantActive,
        bool isProduction)
    {
        if (string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase))
            return !isProduction && grantActive &&
                string.Equals(outputType, PrivatePrintingOutputPolicy.TestVirtual, StringComparison.OrdinalIgnoreCase);

        return string.Equals(outputMode, PrinterTypePolicy.Physical, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(outputType, PrinterTypePolicy.Physical, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidGrantLifetime(DateTimeOffset createdUtc, DateTimeOffset expiresUtc)
    {
        var lifetime = expiresUtc - createdUtc;
        return lifetime >= MinimumGrantLifetime && lifetime <= MaximumGrantLifetime;
    }
}
