namespace SmartPrint.Agent;

public static class AgentCloudHttpTiming
{
    // Keep the relay long-poll shorter than HttpClient.Timeout so the Agent can
    // receive an empty poll response and reset its retry backoff instead of timing out.
    public const int RequestTimeoutSeconds = 20;
    public const int PrivateRelayPendingWaitSeconds = 15;
}
