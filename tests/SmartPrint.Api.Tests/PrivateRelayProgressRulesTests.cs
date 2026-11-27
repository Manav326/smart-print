using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class PrivateRelayProgressRulesTests
{
    [Theory]
    [InlineData(PrivateRelayProgressRules.WaitingForUpload, PrivateRelayProgressRules.ReceivingEncryptedPayload)]
    [InlineData(PrivateRelayProgressRules.ReceivingEncryptedPayload, PrivateRelayProgressRules.WaitingForAgent)]
    [InlineData(PrivateRelayProgressRules.WaitingForAgent, PrivateRelayProgressRules.DownloadingEncryptedPayload)]
    [InlineData(PrivateRelayProgressRules.DownloadingEncryptedPayload, PrivateRelayProgressRules.DecryptingAndAuthenticating)]
    [InlineData(PrivateRelayProgressRules.DecryptingAndAuthenticating, PrivateRelayProgressRules.ValidatingDocument)]
    [InlineData(PrivateRelayProgressRules.ValidatingDocument, PrivateRelayProgressRules.SecuringLocalQueue)]
    [InlineData(PrivateRelayProgressRules.SecuringLocalQueue, PrivateRelayProgressRules.Ready)]
    public void StageRanksFollowThePrivateUploadLifecycle(string current, string next)
    {
        Assert.True(PrivateRelayProgressRules.Rank(next) > PrivateRelayProgressRules.Rank(current));
    }

    [Theory]
    [InlineData(PrivateRelayProgressRules.WaitingForAgent, PrivateRelayProgressRules.DownloadingEncryptedPayload)]
    [InlineData(PrivateRelayProgressRules.DownloadingEncryptedPayload, PrivateRelayProgressRules.ValidatingDocument)]
    [InlineData(PrivateRelayProgressRules.ValidatingDocument, PrivateRelayProgressRules.SecuringLocalQueue)]
    public void AgentCanReportForwardProgress(string current, string requested)
    {
        Assert.True(PrivateRelayProgressRules.CanAdvance(current, requested));
    }

    [Theory]
    [InlineData(PrivateRelayProgressRules.SecuringLocalQueue, PrivateRelayProgressRules.ValidatingDocument)]
    [InlineData(PrivateRelayProgressRules.ValidatingDocument, PrivateRelayProgressRules.DecryptingAndAuthenticating)]
    [InlineData(PrivateRelayProgressRules.WaitingForAgent, PrivateRelayProgressRules.ReceivingEncryptedPayload)]
    [InlineData(PrivateRelayProgressRules.Ready, PrivateRelayProgressRules.SecuringLocalQueue)]
    [InlineData(PrivateRelayProgressRules.Failed, PrivateRelayProgressRules.DownloadingEncryptedPayload)]
    [InlineData(PrivateRelayProgressRules.Expired, PrivateRelayProgressRules.DownloadingEncryptedPayload)]
    public void AgentCannotMoveAStageBackwardsOrReopenATerminalTicket(string current, string requested)
    {
        Assert.False(PrivateRelayProgressRules.CanAdvance(current, requested));
    }

    [Theory]
    [InlineData(PrivateRelayProgressRules.DownloadingEncryptedPayload)]
    [InlineData(PrivateRelayProgressRules.DecryptingAndAuthenticating)]
    [InlineData(PrivateRelayProgressRules.ValidatingDocument)]
    [InlineData(PrivateRelayProgressRules.SecuringLocalQueue)]
    public void OnlyRealAgentProcessingStagesCanBeReported(string stage)
    {
        Assert.True(PrivateRelayProgressRules.IsAgentStage(stage));
    }

    [Theory]
    [InlineData(PrivateRelayProgressRules.WaitingForUpload)]
    [InlineData(PrivateRelayProgressRules.ReceivingEncryptedPayload)]
    [InlineData(PrivateRelayProgressRules.WaitingForAgent)]
    [InlineData(PrivateRelayProgressRules.Ready)]
    [InlineData(PrivateRelayProgressRules.Failed)]
    [InlineData(PrivateRelayProgressRules.Expired)]
    public void LifecycleAndTerminalStagesCannotBeForgedByAgent(string stage)
    {
        Assert.False(PrivateRelayProgressRules.IsAgentStage(stage));
    }

    [Fact]
    public void CustomerMessagesExplainWaitingValidationAndCompletion()
    {
        Assert.Contains("waiting for the shop computer",
            PrivateRelayProgressRules.CustomerMessage(PrivateRelayProgressRules.WaitingForAgent),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checking file security",
            PrivateRelayProgressRules.CustomerMessage(PrivateRelayProgressRules.DecryptingAndAuthenticating),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure upload confirmed",
            PrivateRelayProgressRules.CustomerMessage(PrivateRelayProgressRules.Ready),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("awaiting shop review",
            PrivateRelayProgressRules.CustomerMessage(PrivateRelayProgressRules.Ready),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownStagesAreNotAccepted()
    {
        Assert.Equal(-1, PrivateRelayProgressRules.Rank("UNRECOGNIZED"));
        Assert.False(PrivateRelayProgressRules.CanAdvance("UNRECOGNIZED",
            PrivateRelayProgressRules.DownloadingEncryptedPayload));
    }
}
