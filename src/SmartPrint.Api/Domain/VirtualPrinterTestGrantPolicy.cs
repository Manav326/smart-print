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
            (portName ?? string.Empty).Trim().ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool IsGrantActive(
        Guid tenantId,
        Guid printerTenantId,
        Guid? printerAgentDeviceId,
        Guid grantAgentDeviceId,
        string? printerIdentityFingerprint,
        string grantIdentityFingerprint,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc,
        DateTimeOffset? revokedUtc,
        DateTimeOffset now) =>
        tenantId == printerTenantId &&
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
        DateTimeOffset now)
    {
        if (!string.Equals(outputMode, PrinterTypePolicy.UnsupportedVirtual, StringComparison.OrdinalIgnoreCase) ||
            !grantActive ||
            printerLastSeenUtc is null ||
            printerLastSeenUtc < now - MaximumInventoryAge)
            return false;

        return PrinterTypePolicy.IsWorkingWindowsQueue(status);
    }

    public static bool IsValidGrantLifetime(DateTimeOffset createdUtc, DateTimeOffset expiresUtc)
    {
        var lifetime = expiresUtc - createdUtc;
        return lifetime >= MinimumGrantLifetime && lifetime <= MaximumGrantLifetime;
    }
}
