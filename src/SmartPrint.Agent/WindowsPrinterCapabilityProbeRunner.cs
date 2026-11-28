using System.Text.Json;

namespace SmartPrint.Agent;

/// <summary>
/// Runs driver-owned Win32 DeviceCapabilities and Windows Print Schema calls in
/// a dedicated process per queue. Failures are cached so a crashing driver is
/// not hammered on every inventory refresh.
/// </summary>
public sealed class WindowsPrinterCapabilityProbeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UnknownCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(2);
    private const int MaximumCapabilityJsonCharacters = 15_000;

    private readonly ILogger<WindowsPrinterCapabilityProbeRunner> logger;
    private readonly IAgentChildProcessRunner childProcessRunner;
    private readonly bool executeInCurrentProcess;
    private readonly TimeSpan probeTimeout;
    private readonly SemaphoreSlim concurrencyGate = new(2, 2);
    private readonly object cacheSync = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);

    public WindowsPrinterCapabilityProbeRunner(
        ILogger<WindowsPrinterCapabilityProbeRunner> logger,
        IConfiguration? configuration = null,
        bool executeInCurrentProcess = false,
        IAgentChildProcessRunner? childProcessRunner = null)
    {
        this.logger = logger;
        this.childProcessRunner = childProcessRunner ?? new AgentChildProcessRunner();
        this.executeInCurrentProcess = executeInCurrentProcess;
        probeTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration?.GetValue<int?>("Agent:PrinterCapabilityProbeTimeoutSeconds") ?? 8, 2, 30));
    }

    public Task<string> ProbeAsync(
        string queueName,
        string? driverName,
        CancellationToken cancellationToken) =>
        ProbeAsync(queueName, driverName, null, cancellationToken);

    public async Task<string> ProbeAsync(
        string queueName,
        string? driverName,
        string? portName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(queueName))
            return "{}";

        if (executeInCurrentProcess)
            return WindowsPrinterCapabilityDiscovery.DiscoverJson(queueName, driverName);

        var key = BuildCacheKey(queueName, driverName, portName);
        if (TryGetCached(key, out var cached))
            return cached;

        await concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCached(key, out cached))
                return cached;

            try
            {
                var request = JsonSerializer.Serialize(
                    new PrinterCapabilityProbeRequest(queueName, driverName), JsonOptions);
                var child = await childProcessRunner.ExecuteAsync(
                    "--probe-printer-capabilities", request, probeTimeout, cancellationToken);

                if (child.TimedOut || child.ExitCode != 0 ||
                    child.StandardOutput.Length > MaximumCapabilityJsonCharacters)
                {
                    logger.LogWarning(
                        "Capability probing failed for Windows queue {QueueName} using {DriverName}. ExitCode={ExitCode}; TimedOut={TimedOut}. Capabilities will remain unknown for this queue.",
                        queueName, driverName, child.ExitCode, child.TimedOut);
                    CacheValue(key, "{}", FailureBackoff);
                    return "{}";
                }

                var json = child.StandardOutput.Trim();
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("The capability worker did not return a JSON object.");

                if (document.RootElement.TryGetProperty("queueName", out var returnedQueue) &&
                    returnedQueue.ValueKind == JsonValueKind.String &&
                    !string.Equals(returnedQueue.GetString(), queueName, StringComparison.OrdinalIgnoreCase))
                    throw new JsonException("The capability worker returned a different queue identity.");

                var hasDetails = document.RootElement.EnumerateObject().Any();
                CacheValue(key, json, hasDetails ? SuccessCacheLifetime : UnknownCacheLifetime);
                return json;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "The isolated capability worker failed for Windows queue {QueueName}; other queues will continue to be discovered.",
                    queueName);
                CacheValue(key, "{}", FailureBackoff);
                return "{}";
            }
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    private bool TryGetCached(string key, out string value)
    {
        lock (cacheSync)
        {
            if (cache.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresUtc > DateTimeOffset.UtcNow)
                {
                    value = entry.Json;
                    return true;
                }
                cache.Remove(key);
            }
        }

        value = "{}";
        return false;
    }

    private void CacheValue(string key, string json, TimeSpan lifetime)
    {
        lock (cacheSync)
            cache[key] = new CacheEntry(DateTimeOffset.UtcNow.Add(lifetime), json);
    }

    private static string BuildCacheKey(string queueName, string? driverName, string? portName) =>
        queueName.Trim() + "\u001f" +
        (driverName ?? string.Empty).Trim() + "\u001f" +
        (portName ?? string.Empty).Trim();

    private sealed record CacheEntry(DateTimeOffset ExpiresUtc, string Json);
}

public sealed record PrinterCapabilityProbeRequest(string QueueName, string? DriverName);
