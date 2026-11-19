using System.Net.Http;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentCloudHttpTimingTests
{
    [Fact]
    public void PrivateRelayLongPollCompletesBeforeTheCloudClientTimeout()
    {
        Assert.Equal(20, AgentCloudHttpTiming.RequestTimeoutSeconds);
        Assert.Equal(15, AgentCloudHttpTiming.PrivateRelayPendingWaitSeconds);
        Assert.Equal(
            5,
            AgentCloudHttpTiming.RequestTimeoutSeconds - AgentCloudHttpTiming.PrivateRelayPendingWaitSeconds);
        Assert.Equal(HttpCompletionOption.ResponseHeadersRead, AgentCloudHttpTiming.DocumentResponseCompletionOption);
        Assert.Equal(600, AgentCloudHttpTiming.DocumentTransferTimeoutSeconds);
        Assert.Equal(4, AgentCloudHttpTiming.MaxJobClaimBatchesPerCycle);

        using var transferTimeout = AgentCloudHttpTiming.CreateDocumentTransferCancellationSource(CancellationToken.None);
        Assert.True(transferTimeout.Token.CanBeCanceled);
    }
}
