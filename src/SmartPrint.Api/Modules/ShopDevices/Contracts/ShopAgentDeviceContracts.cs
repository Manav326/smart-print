namespace SmartPrint.Api.Modules.ShopDevices.Contracts;

/// <summary>
/// Tenant-scoped summary returned to shop owners for a paired Windows Print Agent.
/// </summary>
public sealed record ShopAgentDeviceResponse(
    Guid Id,
    string DeviceName,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset? RevokedUtc,
    int PrinterCount,
    string[] LocalEndpoints);

/// <summary>
/// Result of revoking a paired Agent without deleting its history or linked queues.
/// </summary>
public sealed record ShopAgentDeviceRevocationResponse(
    Guid Id,
    string Status,
    DateTimeOffset? RevokedUtc);
