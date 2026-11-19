using System.Net.Http;

namespace SmartPrint.Agent;

public static class AgentCloudHttpTiming
{
    // Keep the relay long-poll shorter than HttpClient.Timeout so the Agent can
    // receive an empty poll response and reset its retry backoff instead of timing out.
    public const int RequestTimeoutSeconds = 20;
    public const int PrivateRelayPendingWaitSeconds = 15;

    // Five-item claims are retained for compatibility and bounded lease duration.
    // Drain at most four batches (the customer batch ceiling) in one work cycle so
    // a 20-file order does not wait for another Agent polling interval.
    public const int MaxJobClaimBatchesPerCycle = 4;

    // Document responses can be much larger than control-plane JSON. With
    // ResponseHeadersRead, HttpClient.Timeout only covers response headers, so
    // the body gets its own bounded transfer token.
    public const int DocumentTransferTimeoutSeconds = 600;
    public const HttpCompletionOption DocumentResponseCompletionOption = HttpCompletionOption.ResponseHeadersRead;

    public static CancellationTokenSource CreateDocumentTransferCancellationSource(
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(DocumentTransferTimeoutSeconds));
        return source;
    }
}
