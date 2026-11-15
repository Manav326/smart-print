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
    }
}
