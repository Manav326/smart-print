using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentPrintLaneSelectorTests
{
    [Fact]
    public void GroupsDistinctWindowsQueuesAndPreservesOrderWithinEachQueue()
    {
        var first = Job("HP Lobby", "SP-1");
        var second = Job("Epson Back Office", "SP-2");
        var third = Job("hp lobby", "SP-3");

        var lanes = AgentPrintLaneSelector.GroupByQueue([first, second, third]);

        Assert.Equal(2, lanes.Count);
        Assert.Equal(["SP-1", "SP-3"], lanes[0].Select(job => job.JobNumber));
        Assert.Equal(["SP-2"], lanes[1].Select(job => job.JobNumber));
    }

    [Fact]
    public void EmptyClaimReturnsNoPrinterLanes()
    {
        Assert.Empty(AgentPrintLaneSelector.GroupByQueue([]));
    }

    private static AgentPrintClaimedJob Job(string queue, string number) =>
        new(Guid.NewGuid(), number, "PRINT_AUTHORISED", queue, "SmartPrint-" + number,
            DateTimeOffset.UtcNow.AddMinutes(1));
}
