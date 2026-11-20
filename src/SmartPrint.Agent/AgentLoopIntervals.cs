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
}
