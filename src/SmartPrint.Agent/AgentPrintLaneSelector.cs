namespace SmartPrint.Agent;

public static class AgentPrintLaneSelector
{
    /// <summary>
    /// Groups claimed jobs by Windows queue. GroupBy preserves input order within
    /// each lane, so each queue remains FIFO while independent queues can run together.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<AgentPrintClaimedJob>> GroupByQueue(
        IEnumerable<AgentPrintClaimedJob> jobs) =>
        jobs.GroupBy(job => job.QueueName, StringComparer.OrdinalIgnoreCase)
            .Select(lane => (IReadOnlyList<AgentPrintClaimedJob>)lane.ToArray())
            .ToArray();
}
