using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using SmartPrint.Documents;

namespace SmartPrint.Agent;

public sealed class Worker(
    ILogger<Worker> logger,
    IHttpClientFactory httpClientFactory,
    AgentCredentialStore credentialStore,
    WindowsPrinterDiscovery printerDiscovery,
    AgentLocalQueueStore localQueueStore,
    WindowsPrintExecutor printExecutor,
    LocalAgentApiState localAgentApiState,
    LocalNetworkManager localNetworkManager,
    AgentRuntimeReadiness runtimeReadiness,
    AgentDocumentEncryptionKeyStore documentEncryptionKeyStore,
    IConfiguration configuration) : BackgroundService
{
    private int printClaimBatchOffset;
    private readonly TimeSpan pollInterval = TimeSpan.FromSeconds(
        Math.Clamp(configuration.GetValue<int?>("Agent:PollIntervalSeconds") ?? 30, 10, 300));
    private readonly TimeSpan healthSyncInterval = TimeSpan.FromSeconds(
        AgentLoopIntervals.ResolveHealthSyncIntervalSeconds(
            configuration.GetValue<int?>("Agent:HealthSyncIntervalSeconds")));
    private readonly string pdfExportDirectory = ResolvePdfExportDirectory(configuration);
    private readonly object retentionPolicySync = new();
    private TimeSpan completedDocumentRetention = TimeSpan.FromDays(
        Math.Clamp(configuration.GetValue<int?>("Agent:CompletedDocumentRetentionDays") ?? 7, 1, 3650));
    private TimeSpan retentionSweepInterval = TimeSpan.FromHours(
        Math.Clamp(configuration.GetValue<int?>("Agent:RetentionSweepHours") ?? 1, 1, 24));
    private bool hasReceivedPlatformRetentionPolicy;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("QRtoPrint Smart Print Agent started.");

        // Keep cloud presence independent from document storage, printer enumeration,
        // queue handoff and potentially slow Windows spooler operations. Each loop has
        // its own retry boundary so one local failure cannot silently stop heartbeats.
        await Task.WhenAll(
            RunHeartbeatLoopAsync(stoppingToken),
            RunPrinterInventoryLoopAsync(stoppingToken),
            RunPrintWorkLoopAsync(stoppingToken),
            RunPrivateRelayLoopAsync(stoppingToken),
            RunPrivateDocumentPurgeLoopAsync(stoppingToken));
    }

    // A PostgreSQL-backed long poll wakes this Agent as soon as its own shop
    // receives a private relay. The bounded server wait is the recovery path if a
    // notification listener is restarting; idle shops no longer poll every 4 seconds.
    private async Task RunPrivateRelayLoopAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        while (!cancellationToken.IsCancellationRequested)
        {
            var pollSucceeded = false;
            try
            {
                var credentials = LoadSecureCredentials();
                if (credentials is not null)
                {
                    using var client = httpClientFactory.CreateClient("cloud");
                    client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");
                    pollSucceeded = await ProcessPrivateCloudRelaysAsync(client, credentials, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Private encrypted relay check failed; the Agent will retry automatically.");
            }

            if (pollSucceeded)
            {
                retryDelay = TimeSpan.FromSeconds(5);
                continue;
            }

            try { await Task.Delay(retryDelay, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            retryDelay = TimeSpan.FromSeconds(Math.Min(60, retryDelay.TotalSeconds * 2));
        }
    }

    private async Task<bool> ProcessPrivateCloudRelaysAsync(
        HttpClient client, AgentCredentials credentials, CancellationToken cancellationToken)
    {
        using var pendingResponse = await SendAgentGetRequestAsync(
            client, credentials, "api/v1/agent/private-relays/pending?waitSeconds=25", cancellationToken);
        if (!pendingResponse.IsSuccessStatusCode)
        {
            if (pendingResponse.StatusCode != HttpStatusCode.NotFound)
                logger.LogWarning("Private relay long-poll returned HTTP {StatusCode}.", (int)pendingResponse.StatusCode);
            return false;
        }

        var pending = await pendingResponse.Content.ReadFromJsonAsync<AgentPrivateRelayPendingResponse>(cancellationToken);
        if (pending?.Items is null || pending.Items.Count == 0) return true;

        var retryableFailure = false;
        foreach (var ticket in pending.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? ciphertext = null;
            byte[]? aesKey = null;
            byte[]? iv = null;
            byte[]? aad = null;
            byte[]? plaintext = null;
            try
            {
                using var contentResponse = await SendAgentGetRequestAsync(
                    client, credentials,
                    $"api/v1/agent/private-relays/{ticket.TicketId:D}/content", cancellationToken);
                if (!contentResponse.IsSuccessStatusCode)
                {
                    logger.LogWarning("Encrypted relay payload for {JobNumber} returned HTTP {StatusCode}.", ticket.JobNumber, (int)contentResponse.StatusCode);
                    retryableFailure = true;
                    continue;
                }

                var wrappedKey = contentResponse.Headers.TryGetValues("X-Print-Encrypted-Key", out var keyValues)
                    ? keyValues.FirstOrDefault() : null;
                var ivText = contentResponse.Headers.TryGetValues("X-Print-Encrypted-Iv", out var ivValues)
                    ? ivValues.FirstOrDefault() : null;
                if (string.IsNullOrWhiteSpace(wrappedKey) || string.IsNullOrWhiteSpace(ivText))
                    throw new InvalidDataException("The encrypted relay envelope is incomplete.");

                ciphertext = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                if (ticket.SizeBytes is <= 0 or > 20L * 1024 * 1024 ||
                    ciphertext.LongLength != ticket.SizeBytes + 16)
                    throw new InvalidDataException("The encrypted relay payload size does not match its ticket.");

                aesKey = documentEncryptionKeyStore.DecryptWrappedAesKey(wrappedKey);
                iv = Convert.FromBase64String(ivText);
                if (aesKey.Length != 32 || iv.Length != 12)
                    throw new CryptographicException("The private relay key envelope has an unsupported size.");

                plaintext = new byte[checked((int)ticket.SizeBytes)];
                aad = Encoding.UTF8.GetBytes(
                    $"SmartPrint.PrivatePrinting.v1|{ticket.TicketId}|{ticket.PrintJobId}|{ticket.SizeBytes}|{ticket.Sha256}");
                using (var aes = new AesGcm(aesKey, 16))
                    aes.Decrypt(iv, ciphertext.AsSpan(0, checked((int)ticket.SizeBytes)),
                        ciphertext.AsSpan(checked((int)ticket.SizeBytes), 16), plaintext, aad);

                var actualHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
                if (!string.Equals(actualHash, ticket.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The decrypted private document failed its SHA-256 check.");

                DocumentAnalysis analysis;
                try { analysis = DocumentAnalyzer.Analyse(plaintext, ticket.FileExtension); }
                catch (DocumentValidationException exception)
                {
                    using var rejected = await SendAgentRequestAsync(
                        client, credentials,
                        $"api/v1/agent/private-relays/{ticket.TicketId:D}/reject",
                        new AgentPrivateRelayRejectRequest(
                            "document_not_supported",
                            exception.Message),
                        cancellationToken);
                    if (!rejected.IsSuccessStatusCode)
                    {
                        logger.LogWarning("Invalid private relay {JobNumber} could not be reported to the API (HTTP {StatusCode}); it will be retried.", ticket.JobNumber, (int)rejected.StatusCode);
                        retryableFailure = true;
                    }
                    else
                    {
                        logger.LogInformation("Private relay {JobNumber} was rejected before entering the payable print queue: {Reason}.", ticket.JobNumber, exception.Message);
                    }
                    continue;
                }

                var job = new AgentClaimedJob(
                    ticket.PrintJobId, ticket.JobNumber, "AWAITING_CONFIGURATION",
                    analysis.PageCount, ticket.Copies, ticket.ColourMode, ticket.PaperSize,
                    ticket.IsDuplex, "INR", 0, ticket.PrinterDeviceId, ticket.PrinterName,
                    ticket.QueueName, "Private document", ticket.MediaType, ticket.SizeBytes,
                    ticket.Sha256, ticket.ExpiresUtc, "LOCAL_AGENT",
                    ticket.OutputMode, ticket.Orientation,
                    PdfBrandingEnabled: false, PrivatePrinting: true, PrintKind: ticket.PrintKind);

                // DPAPI-protect the document on this paired Windows computer before
                // acknowledging the relay. Replaying the same ticket is idempotent.
                await localQueueStore.EnqueueLocalUploadAsync(job, plaintext, cancellationToken);
                using var completion = await SendAgentRequestAsync(
                    client, credentials,
                    $"api/v1/agent/private-relays/{ticket.TicketId:D}/complete",
                    new LocalPrintUploadCompletionRequest(
                        analysis.PageCount, analysis.PageSizesJson, ticket.FileExtension,
                        ticket.MediaType, ticket.SizeBytes, ticket.Sha256),
                    cancellationToken);
                if (!completion.IsSuccessStatusCode)
                {
                    logger.LogWarning("Private relay completion for {JobNumber} returned HTTP {StatusCode}; the protected local copy is retained for retry.", ticket.JobNumber, (int)completion.StatusCode);
                    retryableFailure = true;
                    continue;
                }

                await localQueueStore.MarkLocalUploadFinalizedAsync(ticket.PrintJobId, cancellationToken);
                logger.LogInformation("Private relay {JobNumber} was decrypted and secured in the shop's protected local queue.", ticket.JobNumber);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CryptographicException exception)
            {
                using var rejected = await SendAgentRequestAsync(
                    client, credentials,
                    $"api/v1/agent/private-relays/{ticket.TicketId:D}/reject",
                    new AgentPrivateRelayRejectRequest(
                        "private_document_decryption_failed",
                        "The shop computer could not securely open this file. Please select the document again."),
                    cancellationToken);
                if (!rejected.IsSuccessStatusCode) retryableFailure = true;
                logger.LogWarning(exception, "Private relay {JobNumber} failed authenticated decryption; the file was rejected before payment.", ticket.JobNumber);
            }
            catch (InvalidDataException exception)
            {
                using var rejected = await SendAgentRequestAsync(
                    client, credentials,
                    $"api/v1/agent/private-relays/{ticket.TicketId:D}/reject",
                    new AgentPrivateRelayRejectRequest(
                        "private_document_integrity_failed",
                        "The file did not pass its security and integrity checks. Please select it again."),
                    cancellationToken);
                if (!rejected.IsSuccessStatusCode) retryableFailure = true;
                logger.LogWarning(exception, "Private relay {JobNumber} failed its integrity checks; the file was rejected before payment.", ticket.JobNumber);
            }
            catch (Exception exception)
            {
                retryableFailure = true;
                logger.LogError(exception, "Private relay {JobNumber} could not be staged. Its encrypted cloud copy remains available for a safe retry.", ticket.JobNumber);
            }
            finally
            {
                if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
                if (aesKey is not null) CryptographicOperations.ZeroMemory(aesKey);
                if (iv is not null) CryptographicOperations.ZeroMemory(iv);
                if (aad is not null) CryptographicOperations.ZeroMemory(aad);
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        return !retryableFailure;
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Smart Print API is unavailable. The Agent will retry its heartbeat.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Smart Print heartbeat timed out. The Agent will retry.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected Agent heartbeat error.");
            }

            try
            {
                await Task.Delay(healthSyncInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        AgentCredentials? credentials;
        try
        {
            credentials = LoadSecureCredentials();
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            runtimeReadiness.MarkHeartbeatFailure("identity_unavailable");
            logger.LogError(exception, "The saved Agent identity could not be read. Cloud presence is paused until the owner repairs the local pairing.");
            return;
        }

        if (credentials is null)
        {
            runtimeReadiness.MarkHeartbeatFailure("not_paired");
            logger.LogWarning("This computer is not paired. Connect it from the SmartPrint desktop app.");
            return;
        }

        string? documentEncryptionPublicKey = null;
        try
        {
            documentEncryptionPublicKey = documentEncryptionKeyStore.GetOrCreatePublicKey();
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(exception, "The Agent document-encryption key is unavailable. Private Printing will remain disabled until the local key can be opened.");
        }

        using var client = httpClientFactory.CreateClient("cloud");
        client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");
        using var heartbeat = await SendAgentRequestAsync(
            client,
            credentials,
            "api/v1/agent/heartbeat",
            new
            {
                version = typeof(Worker).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                operatingSystem = Environment.OSVersion.VersionString,
                localEndpoints = GetLocalEndpointsForHeartbeat(),
                documentEncryptionPublicKey
            },
            cancellationToken);

        if (heartbeat.StatusCode == HttpStatusCode.Unauthorized)
        {
            runtimeReadiness.MarkHeartbeatFailure("heartbeat_unauthorized");
            // An invalid/revoked device secret cannot be reused. Remove only the
            // identity that sent this heartbeat; a newer concurrent pairing survives.
            if (credentialStore.DeleteIfMatches(credentials))
            {
                logger.LogError("The shop no longer accepts this Agent identity. Local credentials were cleared; connect this computer again after the shop owner authorises it.");
            }
            else
            {
                logger.LogInformation("A stale heartbeat was rejected after the local Agent identity changed. The newer pairing was preserved.");
            }
            return;
        }

        if (heartbeat.StatusCode == HttpStatusCode.Forbidden)
        {
            runtimeReadiness.MarkHeartbeatFailure("heartbeat_forbidden");
            // A forbidden response may mean the shop is suspended. Retain the
            // identity to avoid accidentally moving this computer to another shop.
            logger.LogWarning("The current shop does not permit this Agent to sync. The saved device identity was retained.");
            return;
        }

        if (!heartbeat.IsSuccessStatusCode)
        {
            runtimeReadiness.MarkHeartbeatFailure($"heartbeat_http_{(int)heartbeat.StatusCode}");
            logger.LogWarning("Agent heartbeat failed with HTTP {StatusCode}.", (int)heartbeat.StatusCode);
            return;
        }

        runtimeReadiness.MarkHeartbeatSuccess();
        var serverConfiguration = await heartbeat.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(
            cancellationToken: cancellationToken);
        if (serverConfiguration is null)
        {
            logger.LogWarning("The Agent heartbeat response was empty; the previous runtime configuration is retained.");
            return;
        }

        if (serverConfiguration.AgentCompletedDocumentRetentionDays is int retentionDays &&
            serverConfiguration.AgentRetentionSweepHours is int sweepHours)
        {
            lock (retentionPolicySync)
            {
                completedDocumentRetention = TimeSpan.FromDays(Math.Clamp(retentionDays, 1, 3650));
                retentionSweepInterval = TimeSpan.FromHours(Math.Clamp(sweepHours, 1, 24));
                hasReceivedPlatformRetentionPolicy = true;
            }
        }

        if (Uri.TryCreate(serverConfiguration.PublicBaseUrl, UriKind.Absolute, out var configuredBaseUri) &&
            configuredBaseUri.Scheme == Uri.UriSchemeHttps &&
            configuredBaseUri.AbsolutePath == "/" &&
            string.IsNullOrEmpty(configuredBaseUri.Query) &&
            string.IsNullOrEmpty(configuredBaseUri.Fragment) &&
            string.IsNullOrEmpty(configuredBaseUri.UserInfo))
        {
            var currentBaseUrl = configuredBaseUri.GetLeftPart(UriPartial.Authority);
            if (!string.Equals(credentials.ApiBaseUrl.TrimEnd('/'), currentBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                credentials = credentials with { ApiBaseUrl = currentBaseUrl };
                credentialStore.Save(credentials);
                logger.LogInformation("Updated the Agent API destination from the canonical HTTPS public URL.");
            }
        }

        var trustedOrigins = serverConfiguration.AllowedPortalOrigins;
        if (trustedOrigins is { Count: > 0 })
        {
            localAgentApiState.SetTrustedPortalOrigins(trustedOrigins);
        }
        else
        {
            localAgentApiState.SetTrustedPortalOrigins([serverConfiguration.PublicBaseUrl ?? string.Empty]);
        }
    }

    private AgentCredentials? LoadSecureCredentials() =>
        credentialStore.LoadForCloudRequests(configuration["Agent:ApiBaseUrl"]);

    private IReadOnlyList<string> GetLocalEndpointsForHeartbeat()
    {
        if (!localAgentApiState.IsRunning) return Array.Empty<string>();
        try
        {
            return localNetworkManager.GetSnapshot().Endpoints;
        }
        catch (Exception exception) when (
            exception is NetworkInformationException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogWarning("The selected shop network could not be read ({FailureType}); the Agent heartbeat will continue without a local endpoint.", exception.GetType().Name);
            return Array.Empty<string>();
        }
    }

    private async Task RunPrinterInventoryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SyncPrinterInventoryAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "The Agent could not synchronize printer inventory; the previous server inventory is retained.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Printer inventory synchronization timed out; the previous server inventory is retained.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected printer inventory synchronization error.");
            }

            try
            {
                await Task.Delay(healthSyncInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SyncPrinterInventoryAsync(CancellationToken cancellationToken)
    {
        AgentCredentials? credentials;
        try
        {
            credentials = LoadSecureCredentials();
        }
        catch (Exception exception) when (
            exception is CryptographicException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            runtimeReadiness.MarkPrinterInventoryFailure("printer_identity_unavailable");
            logger.LogError(exception, "The saved Agent identity could not be read. Printer synchronization is paused.");
            return;
        }

        if (credentials is null)
        {
            runtimeReadiness.MarkPrinterInventoryFailure("printer_agent_unpaired");
            return;
        }
        if (!printerDiscovery.TryDiscover(out var printers))
        {
            runtimeReadiness.MarkPrinterInventoryFailure("printer_discovery_failed");
            logger.LogWarning("Skipping this printer inventory sync because Windows discovery failed; the server's last known queues are retained.");
            return;
        }

        using var client = httpClientFactory.CreateClient("cloud");
        client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");
        using var sync = await SendAgentRequestAsync(
            client,
            credentials,
            "api/v1/agent/printers/sync",
            new { printers },
            cancellationToken);

        if (sync.IsSuccessStatusCode)
        {
            runtimeReadiness.MarkPrinterInventorySuccess(printers.Count);
            logger.LogInformation("Synchronized {PrinterCount} Windows printer queues with the shop.", printers.Count);
        }
        else
        {
            runtimeReadiness.MarkPrinterInventoryFailure($"printer_sync_http_{(int)sync.StatusCode}");
            logger.LogWarning("Printer inventory synchronization failed with HTTP {StatusCode}; the last successful inventory is retained.", (int)sync.StatusCode);
        }
    }

    private async Task RunPrintWorkLoopAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(5);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await localQueueStore.InitializeAsync(cancellationToken);
                runtimeReadiness.SetQueueInitialized(true);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                runtimeReadiness.SetQueueInitialized(false, "queue_initialization_failed");
                logger.LogCritical(exception, "The protected local queue could not be initialized. Print handoff is paused and will retry automatically.");
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
            }
        }

        runtimeReadiness.SetPrintWorkerRunning(true);
        try
        {
        var lastRetentionSweepUtc = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            var sweepNow = DateTimeOffset.UtcNow;
            TimeSpan completedRetention;
            TimeSpan sweepInterval;
            bool hasPolicy;
            lock (retentionPolicySync)
            {
                completedRetention = completedDocumentRetention;
                sweepInterval = retentionSweepInterval;
                hasPolicy = hasReceivedPlatformRetentionPolicy;
            }

            if (hasPolicy && sweepNow - lastRetentionSweepUtc >= sweepInterval)
            {
                try
                {
                    var removed = await localQueueStore.PruneRetainedEntriesAsync(
                        sweepNow, completedRetention, pdfExportDirectory,
                        localQueueStore.EnsurePrivateTestOutputDirectory(), cancellationToken);
                    if (removed > 0)
                        logger.LogInformation("Removed {RemovedCount} expired or retained local document entries.", removed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "The local document retention sweep failed; it will be retried on a later cycle.");
                }
                lastRetentionSweepUtc = sweepNow;
            }

            AgentCredentials? credentials;
            try
            {
                credentials = LoadSecureCredentials();
            }
            catch (Exception exception) when (
                exception is CryptographicException or IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogError(exception, "The saved Agent identity could not be read. Print processing is paused until the owner repairs the local pairing.");
                await localNetworkManager.WaitForSelectionChangeOrDelayAsync(pollInterval, cancellationToken);
                continue;
            }

            if (credentials is null)
            {
                await localNetworkManager.WaitForSelectionChangeOrDelayAsync(pollInterval, cancellationToken);
                continue;
            }

            try
            {
                using var client = httpClientFactory.CreateClient("cloud");
                client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");

                // These operations can take longer than a heartbeat interval. They
                // intentionally run separately so an upload or spooler wait cannot
                // make a healthy Agent appear offline.
                await ProcessLocalQueueHandoffAsync(client, credentials, cancellationToken);
                await ReconcileLegacyPdfQueueJobsAsync(client, credentials, cancellationToken);
                await ReconcileInterruptedPdfExportsAsync(client, credentials, cancellationToken);
                await RetryPendingPdfExportReceiptsAsync(client, credentials, cancellationToken);
                await ReconcileInterruptedSubmissionsAsync(client, credentials, cancellationToken);
                await RetrySpoolerReceiptsAsync(client, credentials, cancellationToken);
                await ProcessAuthorizedPrintJobsAsync(client, credentials, cancellationToken);
                await MonitorSpoolerJobsAsync(client, credentials, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Smart Print API is unavailable. The Agent will retry print handoff without modifying printer queues.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("A print handoff API request timed out. The Agent will retry.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected local print work error.");
            }

            runtimeReadiness.MarkPrintWorkCycle();
            await localNetworkManager.WaitForSelectionChangeOrDelayAsync(pollInterval, cancellationToken);
            }
        }
        finally
        {
            runtimeReadiness.SetPrintWorkerRunning(false);
        }
    }

    private async Task ProcessLocalQueueHandoffAsync(
        HttpClient client,
        AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        await ReconcileStagedLocalUploadsAsync(client, credentials, cancellationToken);
        await ReconcilePendingPaymentLocalUploadsAsync(client, credentials, cancellationToken);
        await RetryPendingAcknowledgementsAsync(client, credentials, cancellationToken);

        using var claimResponse = await SendAgentRequestAsync(
            client,
            credentials,
            "api/v1/agent/jobs/claim",
            new { },
            cancellationToken);
        if (!claimResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("Paid print job claim returned HTTP {StatusCode}.", (int)claimResponse.StatusCode);
            return;
        }

        var claim = await claimResponse.Content.ReadFromJsonAsync<AgentJobClaimResponse>(cancellationToken);
        if (claim?.Jobs is null || claim.Jobs.Count == 0)
        {
            return;
        }

        foreach (var job in claim.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existing = localQueueStore.Find(job.Id);
                if (existing is not null)
                {
                    if (!string.Equals(existing.Sha256, job.Sha256, StringComparison.OrdinalIgnoreCase) ||
                        existing.SizeBytes != job.SizeBytes)
                    {
                        throw new InvalidDataException("The local queue already contains different content for this job ID.");
                    }

                    if (!File.Exists(existing.DocumentPath))
                    {
                        throw new FileNotFoundException("The local queue manifest exists but its protected document is missing.", existing.DocumentPath);
                    }
                    if (job.StorageMode == "LOCAL_AGENT" ||
                        existing.State is "LOCAL_UPLOAD_STAGED" or "LOCAL_UPLOAD_PENDING_PAYMENT" or "ACK_PENDING" or "ACKNOWLEDGED")
                    {
                        // An HTTPS recovery can complete the same ticket while the
                        // Agent still holds the original protected LAN copy. Promote
                        // that copy with the paid job's final print settings.
                        await localQueueStore.UpdateFromClaimAsync(job, cancellationToken);
                    }
                }
                else
                {
                    if (job.StorageMode == "LOCAL_AGENT")
                    {
                        logger.LogError("Local-only document {JobNumber} is missing from this Agent's protected queue. It will not be downloaded from the cloud.", job.JobNumber);
                        continue;
                    }

                    using var documentResponse = await SendAgentGetRequestAsync(
                        client,
                        credentials,
                        $"api/v1/agent/jobs/{job.Id}/document",
                        cancellationToken);
                    if (!documentResponse.IsSuccessStatusCode)
                    {
                        logger.LogWarning("Job {JobNumber} document download returned HTTP {StatusCode}.", job.JobNumber, (int)documentResponse.StatusCode);
                        continue;
                    }

                    var responseHash = documentResponse.Headers.TryGetValues("X-Document-SHA256", out var hashes)
                        ? hashes.FirstOrDefault()
                        : null;
                    var responseSize = documentResponse.Headers.TryGetValues("X-Document-Size", out var sizes)
                        ? sizes.FirstOrDefault()
                        : null;
                    if (!string.Equals(responseHash, job.Sha256, StringComparison.OrdinalIgnoreCase) ||
                        !long.TryParse(responseSize, out var declaredSize) ||
                        declaredSize != job.SizeBytes)
                    {
                        throw new InvalidDataException("The document response headers do not match the claimed job metadata.");
                    }

                    var bytes = await documentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                    try
                    {
                        await localQueueStore.EnqueueAsync(job, bytes, cancellationToken);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }

                await AcknowledgeLocalQueueReceiptAsync(
                    client,
                    credentials,
                    job.Id,
                    job.Sha256,
                    job.SizeBytes,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not secure paid job {JobNumber} in the local queue.", job.JobNumber);
            }
        }
    }

    private async Task RunPrivateDocumentPurgeLoopAsync(CancellationToken cancellationToken)
    {
        // Private source files are deleted immediately after the shop confirms
        // physical output. Keep this cleanup independent from the normal print
        // polling interval (which may be configured as high as five minutes).
        // The short poll means the Agent receives a server confirmation promptly
        // without delaying or duplicating ordinary print work.
        var pollInterval = TimeSpan.FromSeconds(3);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var credentials = LoadSecureCredentials();
                if (credentials is not null)
                {
                    using var client = httpClientFactory.CreateClient("cloud");
                    client.BaseAddress = new Uri(credentials.ApiBaseUrl.TrimEnd('/') + "/");
                    await ReconcilePrivateDocumentPurgesAsync(client, credentials, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Private source-document cleanup check failed; the Agent will retry without delaying normal print work.");
            }

            try { await Task.Delay(pollInterval, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ReconcilePrivateDocumentPurgesAsync(
        HttpClient client,
        AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var response = await SendAgentGetRequestAsync(
            client, credentials, "api/v1/agent/jobs/private-purge", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Private-document purge poll returned HTTP {StatusCode}; source files remain protected.", (int)response.StatusCode);
            return;
        }

        var pending = await response.Content.ReadFromJsonAsync<AgentPrivateDocumentPurgeResponse>(cancellationToken);
        // Also retry local pending acknowledgements. The API may already have
        // redacted the server record even when the previous HTTP response was
        // lost; its duplicate-ack response is idempotent and clears this marker.
        var jobIds = (pending?.JobIds ?? Array.Empty<Guid>())
            .Concat(localQueueStore.GetPrivatePurgePendingJobs().Select(entry => entry.JobId))
            .Distinct()
            .ToArray();
        if (jobIds.Length == 0) return;

        foreach (var jobId in jobIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = localQueueStore.Find(jobId);
                if (entry is null)
                {
                    // The server-authorised source is already absent from this Agent's queue.
                    // Acknowledge absence so the API can redact the source metadata too.
                    // Also remove a possible encrypted file/manifest temporary left by
                    // a process stop between the document write and manifest commit.
                    await localQueueStore.PurgeUnindexedPrivateSourceAsync(jobId, cancellationToken);
                    logger.LogWarning("Private job {JobId} had no queue manifest; orphaned job-specific files were removed before acknowledging the purge.", jobId);
                }
                else
                {
                    if (!entry.PrivatePrinting)
                        throw new InvalidDataException("The server requested a private purge for a non-private local queue item.");
                    if (entry.State is not ("SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED" or "OUTPUT_CONFIRMED" or "PRIVATE_PURGE_PENDING" or "PDF_EXPORT_REPORTED" or "TEST_OUTPUT_SOURCE_PURGED"))
                        throw new InvalidOperationException("The local job has not reached a state that permits a server-confirmed source purge.");

                    if (entry.State is "PDF_EXPORT_REPORTED" or "TEST_OUTPUT_SOURCE_PURGED")
                    {
                        // A verified test PDF is not physical output. Purge only
                        // the protected source while retaining the test artifact
                        // for its separate short retention window.
                        await localQueueStore.PurgePrivateSourceAfterConfirmedOutputAsync(jobId, cancellationToken);
                    }
                    else
                    {
                        if (entry.State is not ("OUTPUT_CONFIRMED" or "PRIVATE_PURGE_PENDING") || entry.CompletedUtc is null)
                            await localQueueStore.MarkOutputConfirmedAsync(jobId, cancellationToken);
                        await localQueueStore.PurgePrivateSourceAfterConfirmedOutputAsync(jobId, cancellationToken);
                    }
                }

                using var acknowledgement = await SendAgentRequestAsync(
                    client, credentials, $"api/v1/agent/jobs/{jobId}/private-document-purged", new { }, cancellationToken);
                if (!acknowledgement.IsSuccessStatusCode)
                {
                    logger.LogWarning("Private-document purge acknowledgement for {JobId} returned HTTP {StatusCode}; it will be retried.", jobId, (int)acknowledgement.StatusCode);
                    continue;
                }

                var current = localQueueStore.Find(jobId);
                if (current?.PrivatePurgePending == true)
                    await localQueueStore.MarkPrivatePurgeAcknowledgedAsync(jobId, cancellationToken);
                logger.LogInformation("Confirmed private source-document removal for job {JobId}.", jobId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Private source-document removal for job {JobId} could not be completed; the next Agent poll will retry.", jobId);
            }
        }
    }

    private async Task ReconcileStagedLocalUploadsAsync(
        HttpClient client,
        AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        foreach (var entry in localQueueStore.GetStagedLocalUploads())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await SendAgentGetRequestAsync(
                    client,
                    credentials,
                    $"api/v1/agent/local-uploads/{entry.JobId}/status",
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Local upload status for {JobNumber} returned HTTP {StatusCode}; keeping the protected file.", entry.JobNumber, (int)response.StatusCode);
                    continue;
                }

                var status = await response.Content.ReadFromJsonAsync<AgentLocalUploadStatusResponse>(cancellationToken);
                if (status is null)
                {
                    logger.LogWarning("Local upload status for {JobNumber} was empty; keeping the protected file.", entry.JobNumber);
                    continue;
                }

                if (status.Completed)
                {
                    await localQueueStore.MarkLocalUploadFinalizedAsync(entry.JobId, cancellationToken);
                }
                else if (status.ExpiresUtc <= DateTimeOffset.UtcNow)
                {
                    if (await localQueueStore.DeleteExpiredLocalUploadAsync(entry.JobId, DateTimeOffset.UtcNow, cancellationToken))
                    {
                        logger.LogInformation("Removed expired unfinalised local upload {JobNumber} from the protected queue.", entry.JobNumber);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not reconcile staged local upload {JobNumber}; keeping its protected file.", entry.JobNumber);
            }
        }
    }

    private async Task ReconcilePendingPaymentLocalUploadsAsync(
        HttpClient client,
        AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        foreach (var entry in localQueueStore.GetPendingPaymentLocalUploads())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await SendAgentGetRequestAsync(
                    client,
                    credentials,
                    $"api/v1/agent/local-uploads/{entry.JobId}/status",
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Pending local upload status for {JobNumber} returned HTTP {StatusCode}; keeping its protected file.", entry.JobNumber, (int)response.StatusCode);
                    continue;
                }

                var status = await response.Content.ReadFromJsonAsync<AgentLocalUploadStatusResponse>(cancellationToken);
                if (status?.SafeToDelete == true && status.DeleteAfterUtc is DateTimeOffset deleteAfterUtc &&
                    deleteAfterUtc <= DateTimeOffset.UtcNow)
                {
                    await localQueueStore.DeleteExpiredPendingPaymentUploadAsync(
                        entry.JobId, deleteAfterUtc, DateTimeOffset.UtcNow, cancellationToken);
                    logger.LogInformation("Removed terminal pending-payment local upload {JobNumber} after the platform retention deadline.", entry.JobNumber);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not reconcile pending-payment local upload {JobNumber}; keeping its protected file.", entry.JobNumber);
            }
        }
    }

    private async Task RetryPendingAcknowledgementsAsync(
        HttpClient client,
        AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        foreach (var entry in localQueueStore.GetPendingAcknowledgements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AcknowledgeLocalQueueReceiptAsync(
                client,
                credentials,
                entry.JobId,
                entry.Sha256,
                entry.SizeBytes,
                cancellationToken);
        }
    }

    private async Task AcknowledgeLocalQueueReceiptAsync(
        HttpClient client,
        AgentCredentials credentials,
        Guid jobId,
        string sha256,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendAgentRequestAsync(
            client,
            credentials,
            $"api/v1/agent/jobs/{jobId}/received",
            new AgentJobReceivedRequest(sha256, sizeBytes),
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await localQueueStore.MarkAcknowledgedAsync(jobId, cancellationToken);
            logger.LogInformation("Job {JobId} is stored in the protected local queue. Physical printing has not been started.", jobId);
            return;
        }

        logger.LogWarning("Local queue receipt for job {JobId} was not acknowledged (HTTP {StatusCode}); it will be retried.", jobId, (int)response.StatusCode);
    }


    private async Task ReconcileInterruptedSubmissionsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetInterruptedSubmissions())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(entry.QueueName) || string.IsNullOrWhiteSpace(entry.SpoolerJobName))
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(entry.JobId, "INTERRUPTED_PRINT_METADATA_MISSING", ct);
                    await ReportOutputUnconfirmedAsync(client, credentials, entry.JobId, "INTERRUPTED_PRINT_METADATA_MISSING", "The local manifest is missing its printer queue or spooler name.", ct);
                    continue;
                }
                var found = printExecutor.FindJobByDocumentName(entry.QueueName, entry.SpoolerJobName);
                if (found is null)
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(entry.JobId, "AGENT_RESTART_DURING_PRINT_SUBMISSION", ct);
                    await ReportOutputUnconfirmedAsync(client, credentials, entry.JobId, "AGENT_RESTART_DURING_PRINT_SUBMISSION", "The agent restarted after submission began and no matching spooler job remains. Automatic retry is disabled.", ct);
                    continue;
                }
                await localQueueStore.MarkSpoolerAcceptedAsync(entry.JobId, found.JobId, entry.SpoolerJobName, ct);
                await ReportSpoolerAcceptedAsync(client, credentials, entry.JobId, found.JobId, entry.SpoolerJobName, found.Status, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Could not reconcile interrupted submission for {JobNumber}.", entry.JobNumber); }
        }
    }

    private async Task RetrySpoolerReceiptsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetSpoolerTrackingJobs())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.SpoolerJobId is > 0 && !string.IsNullOrWhiteSpace(entry.SpoolerJobName))
                await ReportSpoolerAcceptedAsync(client, credentials, entry.JobId, entry.SpoolerJobId.Value, entry.SpoolerJobName, entry.SpoolerStatus ?? "QUEUED", ct);
        }
    }

    private async Task ProcessAuthorizedPrintJobsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        var acknowledgedJobs = localQueueStore.GetAcknowledgedJobs();
        var batch = AgentLocalQueueBatchSelector.SelectNext(acknowledgedJobs, ref printClaimBatchOffset, 100);
        var proofs = batch.Select(x => new AgentLocalQueueProof(x.JobId, x.Sha256, x.SizeBytes)).ToArray();
        if (proofs.Length == 0) return;
        using var response = await SendAgentRequestAsync(client, credentials, "api/v1/agent/jobs/print-claim", new AgentPrintClaimRequest(proofs), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Print dispatch claim returned HTTP {StatusCode}.", (int)response.StatusCode);
            return;
        }
        var result = await response.Content.ReadFromJsonAsync<AgentPrintClaimResponse>(ct);
        if (result?.Jobs is null) return;

        foreach (var job in result.Jobs)
        {
            ct.ThrowIfCancellationRequested();
            var entry = localQueueStore.Find(job.Id);
            var isPdfExport = AgentOutputModeRules.IsPdfExport(job.OutputMode);
            var localStateValid = entry is not null &&
                (entry.State == "ACKNOWLEDGED" ||
                 (isPdfExport && entry.State == "OUTPUT_UNCONFIRMED"));
            if (entry is null || !localStateValid ||
                !string.Equals(entry.QueueName, job.QueueName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(job.SpoolerJobName))
            {
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, "LOCAL_QUEUE_PROOF_MISMATCH", "The local queue item no longer matches the output assignment.", CancellationToken.None);
                continue;
            }
            try
            {
                var rejectedPages = job.RejectedPageNumbers ?? Array.Empty<int>();
                await localQueueStore.UpdateRejectedPagesAsync(job.Id, rejectedPages, ct);
                entry = localQueueStore.Find(job.Id)
                    ?? throw new FileNotFoundException("The local print job disappeared before output.");

                if (isPdfExport)
                {
                    var privateTestOutput = entry.PrivatePrinting;
                    var exportDirectory = privateTestOutput
                        ? localQueueStore.EnsurePrivateTestOutputDirectory()
                        : pdfExportDirectory;
                    var exportEntry = entry with { OutputMode = PrinterOutputModeRules.PdfExport, RejectedPageNumbers = rejectedPages };
                    var destination = printExecutor.GetPdfExportPath(exportEntry, exportDirectory);
                    await localQueueStore.MarkPdfExportStartedAsync(job.Id, destination, ct);
                    var documentBytes = localQueueStore.ReadDocument(job.Id);
                    PdfExportExecutionResult exportResult;
                    try
                    {
                        exportResult = await printExecutor.ExportPdfUsingWindowsPrinterOrFallbackAsync(
                            exportEntry, documentBytes, destination, exportDirectory, ct);
                    }
                    finally { CryptographicOperations.ZeroMemory(documentBytes); }

                    var receipt = exportResult.Receipt;
                    await localQueueStore.MarkPdfExportedAsync(job.Id, receipt, ct);
                    await ReportPdfExportedAsync(client, credentials, job.Id, receipt, ct);
                    logger.LogInformation(
                        privateTestOutput
                            ? "Private test output for {JobNumber} was saved locally as {FileName}; it is not physical fulfilment. The protected source remains pending platform-authorised cleanup."
                            : "Job {JobNumber} was exported as {FileName} to {OutputPath} using {ExportMethod}; spooler job {SpoolerJobId}.",
                        job.JobNumber, receipt.FileName, receipt.OutputPath, exportResult.Method, exportResult.SpoolerJobId);
                    continue;
                }

                await localQueueStore.MarkSubmissionStartedAsync(job.Id, job.SpoolerJobName, ct);
                var bytes = localQueueStore.ReadDocument(job.Id);
                SpoolerPrintReceipt spoolerReceipt;
                try { spoolerReceipt = printExecutor.Print(entry with { SpoolerJobName = job.SpoolerJobName, RejectedPageNumbers = rejectedPages }, bytes, ct); }
                finally { CryptographicOperations.ZeroMemory(bytes); }

                await localQueueStore.MarkSpoolerAcceptedAsync(job.Id, spoolerReceipt.JobId, spoolerReceipt.DocumentName, ct);
                await ReportSpoolerAcceptedAsync(client, credentials, job.Id, spoolerReceipt.JobId, spoolerReceipt.DocumentName, "QUEUED", ct);
            }
            catch (PrintExecutionBlockedException ex)
            {
                await localQueueStore.MarkReadyAfterBlockedAsync(job.Id, ex.ReasonCode, CancellationToken.None);
                await ReportPrintBlockedAsync(client, credentials, job.Id, ex.ReasonCode, ex.Message, CancellationToken.None);
                logger.LogWarning(ex, "Pre-submission output block for {JobNumber}.", job.JobNumber);
            }
            catch (PrintSubmissionUncertainException ex)
            {
                await localQueueStore.MarkOutputUnconfirmedAsync(
                    job.Id, ex.ReasonCode, CancellationToken.None,
                    isPdfExport ? PrinterOutputModeRules.PdfExport : null);
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, ex.ReasonCode, ex.Message, CancellationToken.None);
                logger.LogError(ex, "Uncertain output for {JobNumber}; no automatic retry.", job.JobNumber);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(
                        job.Id, "AGENT_STOPPED_DURING_OUTPUT", CancellationToken.None,
                        isPdfExport ? PrinterOutputModeRules.PdfExport : null);
                }
                catch (Exception persistError) { logger.LogCritical(persistError, "Could not persist interrupted output state."); }
                await ReportOutputUnconfirmedAsync(client, credentials, job.Id, "AGENT_STOPPED_DURING_OUTPUT", "The agent stopped during output. Inspect the saved PDF or physical printer output before retrying.", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(
                        job.Id, isPdfExport ? "PDF_EXPORT_OUTCOME_UNCERTAIN" : "UNEXPECTED_PRINT_EXECUTION_ERROR",
                        CancellationToken.None, isPdfExport ? PrinterOutputModeRules.PdfExport : null);
                }
                catch (Exception persistError) { logger.LogCritical(persistError, "Could not persist uncertain output state."); }
                await ReportOutputUnconfirmedAsync(
                    client, credentials, job.Id,
                    isPdfExport ? "PDF_EXPORT_OUTCOME_UNCERTAIN" : "UNEXPECTED_PRINT_EXECUTION_ERROR",
                    ex.Message, CancellationToken.None);
                logger.LogError(ex, "Unexpected output execution error for {JobNumber}; no automatic retry.", job.JobNumber);
            }
        }
    }

    private async Task ReconcileLegacyPdfQueueJobsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetLegacyPdfQueueJobs())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await localQueueStore.MarkOutputUnconfirmedAsync(
                    entry.JobId, "LEGACY_MICROSOFT_PDF_DIALOG_INTERRUPTED", ct,
                    PrinterOutputModeRules.PdfExport);
                await ReportOutputUnconfirmedAsync(
                    client, credentials, entry.JobId, "LEGACY_MICROSOFT_PDF_DIALOG_INTERRUPTED",
                    "The previous Microsoft Print to PDF attempt used an interactive Windows Save As dialog and cannot be reconciled by the background service. Confirm that no PDF was saved before retrying with direct PDF export.",
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Could not reconcile legacy PDF printer job {JobNumber}.", entry.JobNumber); }
        }
    }

    private async Task ReconcileInterruptedPdfExportsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetPdfExportRecoveryJobs())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(entry.OutputFilePath)) continue;
                if (File.Exists(entry.OutputFilePath))
                {
                    var receipt = printExecutor.ReadExistingPdfExport(entry.OutputFilePath);
                    await localQueueStore.MarkPdfExportedAsync(entry.JobId, receipt, ct);
                    await ReportPdfExportedAsync(client, credentials, entry.JobId, receipt, ct);
                }
                else
                {
                    await localQueueStore.MarkPdfExportReadyToRetryAsync(entry.JobId, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not reconcile PDF export for {JobNumber}; no automatic overwrite will occur.", entry.JobNumber);
            }
        }
    }

    private async Task RetryPendingPdfExportReceiptsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetPendingPdfExportReceipts())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.OutputFilePath) ||
                string.IsNullOrWhiteSpace(entry.OutputFileName) ||
                string.IsNullOrWhiteSpace(entry.OutputFileSha256) ||
                entry.OutputFileSizeBytes is not > 0) continue;
            await ReportPdfExportedAsync(client, credentials, entry.JobId,
                new PdfExportReceipt(entry.OutputFileName, entry.OutputFilePath,
                    entry.OutputFileSizeBytes.Value, entry.OutputFileSha256), ct);
        }
    }

    private async Task ReportPdfExportedAsync(
        HttpClient client, AgentCredentials credentials, Guid jobId, PdfExportReceipt receipt, CancellationToken ct)
    {
        var entry = localQueueStore.Find(jobId);
        using var response = entry?.PrivatePrinting == true
            ? await SendAgentRequestAsync(
                client, credentials, $"api/v1/agent/jobs/{jobId}/private-test-output",
                new AgentPrivateTestOutputRequest(receipt.FileName, receipt.SizeBytes, receipt.Sha256), ct)
            : await SendAgentRequestAsync(
                client, credentials, $"api/v1/agent/jobs/{jobId}/pdf-exported",
                new AgentPdfExportedRequest(receipt.FileName, receipt.OutputPath, receipt.SizeBytes, receipt.Sha256), ct);
        if (response.IsSuccessStatusCode)
        {
            if (entry?.State == "PDF_EXPORTED")
                await localQueueStore.MarkPdfExportReceiptReportedAsync(jobId, ct);
        }
        else logger.LogWarning("PDF output receipt for {JobId} returned HTTP {StatusCode}; it will be retried.", jobId, (int)response.StatusCode);
    }

    private static string ResolvePdfExportDirectory(IConfiguration configuration)
    {
        var configured = configuration["Agent:PdfExportDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        var publicDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (string.IsNullOrWhiteSpace(publicDocuments))
            publicDocuments = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QRtoPrint", "Exports");
        return Path.Combine(publicDocuments, "QRtoPrint Exports");
    }

    private async Task MonitorSpoolerJobsAsync(HttpClient client, AgentCredentials credentials, CancellationToken ct)
    {
        foreach (var entry in localQueueStore.GetSpoolerTrackingJobs())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.SpoolerJobId is not > 0 || string.IsNullOrWhiteSpace(entry.QueueName)) continue;
            try
            {
                var found = printExecutor.GetJob(entry.QueueName, entry.SpoolerJobId.Value);
                if (found is null)
                {
                    await localQueueStore.MarkOutputUnconfirmedAsync(entry.JobId, "SPOOLER_JOB_NO_LONGER_VISIBLE", ct);
                    await ReportOutputUnconfirmedAsync(client, credentials, entry.JobId, "SPOOLER_JOB_NO_LONGER_VISIBLE", "Windows no longer lists the spooler job; check actual paper output.", ct);
                    continue;
                }
                await localQueueStore.UpdateSpoolerStatusAsync(entry.JobId, found.Status, ct);
                using var status = await SendAgentRequestAsync(client, credentials, $"api/v1/agent/jobs/{entry.JobId}/spooler-status",
                    new AgentSpoolerStatusRequest(found.JobId, found.Status, found.PagesPrinted, found.TotalPages), ct);
                if (!status.IsSuccessStatusCode)
                {
                    logger.LogWarning("Spooler status returned HTTP {StatusCode}.", (int)status.StatusCode);
                }
                else
                {
                    var cloudState = await status.Content.ReadFromJsonAsync<AgentCloudSpoolerStatusResponse>(ct);
                    if (cloudState?.Status == "COMPLETED")
                        await localQueueStore.MarkOutputConfirmedAsync(entry.JobId, ct);
                }
            }
            catch (Win32Exception ex)
            {
                logger.LogWarning(ex, "Could not inspect Windows print job {SpoolerJobId}.", entry.SpoolerJobId);
                try
                {
                    await localQueueStore.UpdateSpoolerStatusAsync(entry.JobId, "PRINTER_UNAVAILABLE", ct);
                    using var status = await SendAgentRequestAsync(
                        client,
                        credentials,
                        $"api/v1/agent/jobs/{entry.JobId}/spooler-status",
                        new AgentSpoolerStatusRequest(entry.SpoolerJobId.Value, "PRINTER_UNAVAILABLE", 0, 0),
                        ct);
                    if (!status.IsSuccessStatusCode)
                        logger.LogWarning("Printer-unavailable status for {JobNumber} returned HTTP {StatusCode}.", entry.JobNumber, (int)status.StatusCode);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception reportException)
                {
                    logger.LogWarning(reportException, "Could not report printer-unavailable state for {JobNumber}.", entry.JobNumber);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Spooler monitor error for {JobNumber}.", entry.JobNumber); }
        }
    }

    private async Task ReportSpoolerAcceptedAsync(HttpClient client, AgentCredentials credentials, Guid jobId, int spoolerId, string spoolerName, string status, CancellationToken ct)
    {
        using var response = await SendAgentRequestAsync(client, credentials, $"api/v1/agent/jobs/{jobId}/spooler-accepted",
            new AgentSpoolerAcceptedRequest(spoolerId, spoolerName, status), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Spooler acceptance for {JobId} returned HTTP {StatusCode}; will retry.", jobId, (int)response.StatusCode);
            return;
        }
        var cloudState = await response.Content.ReadFromJsonAsync<AgentCloudSpoolerStatusResponse>(ct);
        if (cloudState?.Status == "COMPLETED")
            await localQueueStore.MarkOutputConfirmedAsync(jobId, ct);
    }

    private async Task ReportPrintBlockedAsync(HttpClient client, AgentCredentials credentials, Guid jobId, string code, string message, CancellationToken ct)
    {
        using var response = await SendAgentRequestAsync(client, credentials, $"api/v1/agent/jobs/{jobId}/print-blocked",
            new AgentPrintBlockedRequest(code, message.Length > 300 ? message[..300] : message), ct);
        if (!response.IsSuccessStatusCode) logger.LogWarning("Print block for {JobId} returned HTTP {StatusCode}.", jobId, (int)response.StatusCode);
    }

    private async Task ReportOutputUnconfirmedAsync(HttpClient client, AgentCredentials credentials, Guid jobId, string code, string message, CancellationToken ct)
    {
        using var response = await SendAgentRequestAsync(client, credentials, $"api/v1/agent/jobs/{jobId}/output-unconfirmed",
            new AgentPrintBlockedRequest(code, message.Length > 300 ? message[..300] : message), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Output reconciliation for {JobId} returned HTTP {StatusCode}.", jobId, (int)response.StatusCode);
            return;
        }
        var cloudState = await response.Content.ReadFromJsonAsync<AgentCloudSpoolerStatusResponse>(ct);
        if (cloudState?.Status == "COMPLETED")
            await localQueueStore.MarkOutputConfirmedAsync(jobId, ct);
    }

    private static async Task<HttpResponseMessage> SendAgentGetRequestAsync(
        HttpClient client,
        AgentCredentials credentials,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
        request.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAgentRequestAsync<T>(
        HttpClient client,
        AgentCredentials credentials,
        string path,
        T payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Print-Agent-Id", credentials.AgentDeviceId.ToString());
        request.Headers.Add("X-Print-Agent-Secret", credentials.AgentSecret);
        return await client.SendAsync(request, cancellationToken);
    }
}
