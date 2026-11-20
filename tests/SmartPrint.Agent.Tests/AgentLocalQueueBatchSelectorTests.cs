using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentLocalQueueBatchSelectorTests
{
    [Fact]
    public void SelectNext_ReachesJobsBeyondTheFirstHundred()
    {
        var jobs = Enumerable.Range(0, 101).ToArray();
        var nextIndex = 0;

        var first = AgentLocalQueueBatchSelector.SelectNext(jobs, ref nextIndex, 100);
        var second = AgentLocalQueueBatchSelector.SelectNext(jobs, ref nextIndex, 100);

        Assert.Equal(100, first.Count);
        Assert.DoesNotContain(100, first);
        Assert.Contains(100, second);
    }

    [Fact]
    public void SelectNext_RotatesAcrossTheWholeQueueWithoutSkippingItems()
    {
        var jobs = Enumerable.Range(0, 250).ToArray();
        var nextIndex = 0;
        var visited = new List<int>();

        for (var attempt = 0; visited.Count < jobs.Length && attempt < 10; attempt++)
        {
            visited.AddRange(AgentLocalQueueBatchSelector.SelectNext(jobs, ref nextIndex, 100));
        }

        Assert.Equal(jobs.Length, visited.Count);
        Assert.Equal(jobs.Order(), visited.Order());
    }

    [Fact]
    public void SelectNext_IncludesAllTwentyJobsFromAnAcceptedPrintBatch()
    {
        var jobs = Enumerable.Range(0, 20).Select(index => $"batch-job-{index}").ToArray();
        var nextIndex = 0;

        var selected = AgentLocalQueueBatchSelector.SelectNext(jobs, ref nextIndex, 100);

        Assert.Equal(20, selected.Count);
        Assert.Equal(jobs, selected);
    }

    [Fact]
    public void SelectNext_ResetsCursorWhenQueueIsEmptyAndRejectsInvalidBatchSize()
    {
        var nextIndex = 125;

        Assert.Empty(AgentLocalQueueBatchSelector.SelectNext(Array.Empty<int>(), ref nextIndex, 100));
        Assert.Equal(0, nextIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgentLocalQueueBatchSelector.SelectNext(new[] { 1 }, ref nextIndex, 0));
    }
}
