using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.ShopDevices.Contracts;
using SmartPrint.Api.Modules.ShopDevices.Domain;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Modules.ShopDevices.Services;

/// <summary>
/// Coordinates shop-side pairing, discovery-status reads and revocation for Windows Print Agents.
/// </summary>
public sealed class ShopAgentDevicesService(SmartPrintDbContext db)
{
    /// <summary>
    /// Creates a ten-minute enrollment code and persists only its one-way hash.
    /// </summary>
    public async Task<CreateAgentEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
        Guid tenantId,
        Guid? actorId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var code = Base64UrlTokenEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var enrollment = new AgentEnrollmentCode
        {
            TenantId = tenantId,
            CodeHash = OpaqueTokenHasher.Hash(code),
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(10),
            CreatedByUserId = actorId
        };

        db.AgentEnrollmentCodes.Add(enrollment);
        await db.SaveChangesAsync(ct);
        return new CreateAgentEnrollmentCodeResponse(code, enrollment.ExpiresUtc);
    }

    /// <summary>
    /// Lists only the authenticated tenant's Agents and derives online status from a fresh heartbeat.
    /// </summary>
    public async Task<IReadOnlyList<ShopAgentDeviceResponse>> GetDevicesAsync(Guid tenantId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var onlineAfter = now.AddMinutes(-2);
        var devices = await db.AgentDevices.AsNoTracking()
            .Where(device => device.TenantId == tenantId)
            .OrderByDescending(device => device.LastSeenUtc)
            .Select(device => new AgentDeviceSnapshot(
                device.Id,
                device.DeviceName,
                device.RevokedUtc != null ? "Revoked" :
                    device.LastSeenUtc != null && device.LastSeenUtc >= onlineAfter ? "Online" : "Offline",
                device.CreatedUtc,
                device.LastSeenUtc,
                device.RevokedUtc,
                db.PrinterDevices.Count(printer =>
                    printer.TenantId == tenantId && printer.AgentDeviceId == device.Id),
                device.LocalEndpointsJson))
            .ToListAsync(ct);

        return devices.Select(device => new ShopAgentDeviceResponse(
            device.Id,
            device.DeviceName,
            device.Status,
            device.CreatedUtc,
            device.LastSeenUtc,
            device.RevokedUtc,
            device.PrinterCount,
            ShopAgentDeviceRules.ReadLocalAgentEndpoints(device.LocalEndpointsJson)))
            .ToArray();
    }

    /// <summary>
    /// Revokes an Agent and marks its linked printer queues unusable, retaining the existing records.
    /// </summary>
    public async Task<ShopAgentDeviceRevocationResponse?> RevokeAsync(
        Guid tenantId,
        Guid agentId,
        CancellationToken ct)
    {
        var device = await db.AgentDevices.SingleOrDefaultAsync(
            item => item.Id == agentId && item.TenantId == tenantId, ct);
        if (device is null)
            return null;

        if (device.RevokedUtc is null)
        {
            device.RevokedUtc = DateTimeOffset.UtcNow;
            device.Status = "Revoked";
            await db.PrinterDevices
                .Where(printer => printer.TenantId == tenantId && printer.AgentDeviceId == agentId)
                .ExecuteUpdateAsync(update => update.SetProperty(printer => printer.Status, "AgentRevoked"), ct);
            await db.SaveChangesAsync(ct);
        }

        return new ShopAgentDeviceRevocationResponse(device.Id, device.Status, device.RevokedUtc);
    }

    private sealed record AgentDeviceSnapshot(
        Guid Id,
        string DeviceName,
        string Status,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? LastSeenUtc,
        DateTimeOffset? RevokedUtc,
        int PrinterCount,
        string? LocalEndpointsJson);
}
