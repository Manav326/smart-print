using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

/// <summary>
/// Validates Agent-reported relay progress and whether an encrypted payload has
/// actually reached a durable cloud relay or a live direct-LAN claim.
/// </summary>
public static class AgentPrivateRelayStageRules
{
    public static string? NormalizeRequestedStage(string? stage) =>
        string.IsNullOrWhiteSpace(stage) ? null : stage.Trim().ToUpperInvariant();

    public static bool HasReceivedPayload(
        DateTimeOffset? cloudRelayUploadedUtc,
        string? cloudRelayStorageKey,
        DateTimeOffset? claimedUtc,
        int uploadAttempts,
        DateTimeOffset? claimLeaseExpiresUtc,
        DateTimeOffset now)
    {
        var hasStoredCloudRelay = cloudRelayUploadedUtc is not null &&
            !string.IsNullOrWhiteSpace(cloudRelayStorageKey);
        var hasClaimedDirectLanUpload = claimedUtc is not null &&
            uploadAttempts > 0 &&
            claimLeaseExpiresUtc > now;
        return hasStoredCloudRelay || hasClaimedDirectLanUpload;
    }
}
