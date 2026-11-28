using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentLoopIntervalsTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(30, true)]
    public void RefreshesQueueMetadataAfterSlowCapabilityProbesOnly(int elapsedSeconds, bool expected)
    {
        Assert.Equal(
            expected,
            AgentLoopIntervals.ShouldRefreshPrinterMetadataAfterCapabilityProbe(TimeSpan.FromSeconds(elapsedSeconds)));
    }

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

    [Theory]
    [InlineData(null, 4)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(8, 8)]
    [InlineData(20, 8)]
    public void PrinterLaneConcurrencyIsBounded(int? configured, int expected)
    {
        Assert.Equal(expected, AgentLoopIntervals.ResolvePrinterLaneConcurrency(configured));
    }

    [Theory]
    [InlineData(1, 2000)]
    [InlineData(2, 4000)]
    [InlineData(3, 8000)]
    [InlineData(4, 16000)]
    [InlineData(5, 30000)]
    [InlineData(20, 30000)]
    public void PrivateRelayRetryBackoffIsBoundedAndExponential(int failures, int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds,
            AgentLoopIntervals.ResolvePrivateRelayRetryDelayMilliseconds(failures));
    }

    [Fact]
    public void PrivateRelayRetryBackoffUsesBoundedJitter()
    {
        Assert.Equal(1600, AgentLoopIntervals.ResolvePrivateRelayRetryDelayMilliseconds(1, 0));
        Assert.Equal(2000, AgentLoopIntervals.ResolvePrivateRelayRetryDelayMilliseconds(1, 0.5));
        Assert.Equal(2400, AgentLoopIntervals.ResolvePrivateRelayRetryDelayMilliseconds(1, 1));
        Assert.Equal(30000, AgentLoopIntervals.ResolvePrivateRelayRetryDelayMilliseconds(5, 1));
    }
}
