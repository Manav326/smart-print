using SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentPrivateRelayStageRulesTests
{
    [Theory]
    [InlineData(" downloading_encrypted_payload ", "DOWNLOADING_ENCRYPTED_PAYLOAD")]
    [InlineData("Validating_Document", "VALIDATING_DOCUMENT")]
    [InlineData("SECURING_LOCAL_QUEUE", "SECURING_LOCAL_QUEUE")]
    public void NormalizesAgentProgressWithoutChangingTheStageMeaning(string input, string expected)
    {
        Assert.Equal(expected, AgentPrivateRelayStageRules.NormalizeRequestedStage(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyStageIsRejectedBeforeItReachesTheProgressStateMachine(string? input)
    {
        Assert.Null(AgentPrivateRelayStageRules.NormalizeRequestedStage(input));
    }

    [Fact]
    public void AcceptsOnlyAStoredCloudRelayOrAnUnexpiredDirectLanClaim()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(AgentPrivateRelayStageRules.HasReceivedPayload(
            now, "tenant/relay/opaque-key", null, 0, null, now));

        Assert.False(AgentPrivateRelayStageRules.HasReceivedPayload(
            now, " ", null, 0, null, now));

        Assert.True(AgentPrivateRelayStageRules.HasReceivedPayload(
            null, null, now.AddMinutes(-1), 1, now.AddMinutes(1), now));

        Assert.False(AgentPrivateRelayStageRules.HasReceivedPayload(
            null, null, now.AddMinutes(-1), 1, now, now));

        Assert.False(AgentPrivateRelayStageRules.HasReceivedPayload(
            null, null, now, 0, now.AddMinutes(1), now));

        Assert.False(AgentPrivateRelayStageRules.HasReceivedPayload(
            null, null, null, 2, now.AddMinutes(1), now));
    }
}
