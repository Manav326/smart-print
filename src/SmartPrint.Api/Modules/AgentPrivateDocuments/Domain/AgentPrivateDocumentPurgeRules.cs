using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

public enum AgentPrivateDocumentPurgeAuthorizationFailure
{
    None,
    NotPrivatePrinting,
    OutputNotVerified,
    PurgeNotRequested,
    AgentMismatch
}

/// <summary>
/// Guards acknowledgement of local-only source deletion after verified output.
/// </summary>
public static class AgentPrivateDocumentPurgeRules
{
    public static AgentPrivateDocumentPurgeAuthorizationFailure ValidateAcknowledgement(
        bool privatePrinting,
        string? status,
        string? outputType,
        DateTimeOffset? outputConfirmedUtc,
        DateTimeOffset? outputCompletedUtc,
        DateTimeOffset? purgeRequestedUtc,
        Guid? claimedByAgentDeviceId,
        Guid acknowledgingAgentDeviceId)
    {
        if (!privatePrinting)
            return AgentPrivateDocumentPurgeAuthorizationFailure.NotPrivatePrinting;

        var physicalOutputConfirmed =
            status == "COMPLETED" &&
            outputType == PrivatePrintingOutputPolicy.Physical &&
            outputConfirmedUtc is not null;

        var testOutputVerified =
            status == "TEST_OUTPUT_READY" &&
            outputType == PrivatePrintingOutputPolicy.TestVirtual &&
            outputCompletedUtc is not null;

        if (!physicalOutputConfirmed && !testOutputVerified)
            return AgentPrivateDocumentPurgeAuthorizationFailure.OutputNotVerified;

        if (purgeRequestedUtc is null)
            return AgentPrivateDocumentPurgeAuthorizationFailure.PurgeNotRequested;

        if (claimedByAgentDeviceId != acknowledgingAgentDeviceId)
            return AgentPrivateDocumentPurgeAuthorizationFailure.AgentMismatch;

        return AgentPrivateDocumentPurgeAuthorizationFailure.None;
    }
}
