using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentLoopIntervalsTests
{
    [Theory]
    [InlineData(null, 30)]
    [InlineData(1, 10)]
    [InlineData(10, 10)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(300, 60)]
    public void HealthSyncIntervalStaysInsideTheServerFreshnessWindow(int? configured, int expected)
    {
        Assert.Equal(expected, AgentLoopIntervals.ResolveHealthSyncIntervalSeconds(configured));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(15, 15)]
    [InlineData(300, 30)]
    public void PrintWorkFallbackPollStaysBoundedWhenNotificationsAreUnavailable(int? configured, int expected)
    {
        Assert.Equal(expected, AgentLoopIntervals.ResolvePrintWorkPollIntervalSeconds(configured));
    }
}
