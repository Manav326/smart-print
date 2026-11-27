namespace SmartPrint.Agent;

public static class AgentLoopIntervals
{
    // The API considers an Agent and its printer inventory fresh for two minutes.
    // Keep health sync at no more than one minute so a missed cycle can be tolerated.
    public static int ResolveHealthSyncIntervalSeconds(int? configuredSeconds) =>
        Math.Clamp(configuredSeconds ?? 30, 10, 60);

    // Notifications normally wake printing immediately. This is the bounded
    // durable polling fallback when a notification is missed or unavailable.
    public static int ResolvePrintWorkPollIntervalSeconds(int? configuredSeconds) =>
        Math.Clamp(configuredSeconds ?? 5, 2, 30);

    // Keep cross-printer submission concurrency bounded so a slow/broken queue
    // cannot monopolise the Agent, while multiple selected printers make progress.
    public static int ResolvePrinterLaneConcurrency(int? configuredLanes) =>
        Math.Clamp(configuredLanes ?? 4, 1, 8);

    // Private-relay failures should recover faster than the general Agent loop,
    // while bounded exponential backoff and jitter prevent retry storms.
    public static int ResolvePrivateRelayRetryDelayMilliseconds(int consecutiveFailures, double jitterSample = 0.5)
    {
        var failures = Math.Clamp(consecutiveFailures, 1, 16);
        var baseDelayMilliseconds = Math.Min(30_000, 2_000 * (1 << Math.Min(failures - 1, 4)));
        var jitter = 0.8 + (Math.Clamp(jitterSample, 0, 1) * 0.4);
        return Math.Clamp((int)Math.Round(baseDelayMilliseconds * jitter), 1_000, 30_000);
    }
}
