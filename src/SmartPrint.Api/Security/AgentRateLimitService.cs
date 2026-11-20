namespace SmartPrint.Api.Security;

public enum AgentTrafficClass
{
    Standard,
    Priority,
    BulkReconciliation
}

/// <summary>
/// Per-device Agent API throttling with a protected priority reserve.
/// Bulk reconciliation cannot consume the final reserved tokens needed by
/// heartbeat, inventory, print claims and durable output receipts.
/// </summary>
public sealed class AgentRateLimitService
{
    public const string AuthenticatedAgentItemKey = "SmartPrint.AuthenticatedAgentDevice";

    public const int BurstCapacity = 60;
    public const int TokensPerReplenishment = 30;
    public static readonly TimeSpan ReplenishmentPeriod = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan InactiveEntryRetention = TimeSpan.FromMinutes(2);

    public const int PriorityReserveCapacity = 12;
    public const int BulkBurstCapacity = 30;
    public static readonly TimeSpan BulkReplenishmentPeriod = TimeSpan.FromSeconds(30);

    private static readonly double TokensPerSecond =
        TokensPerReplenishment / ReplenishmentPeriod.TotalSeconds;
    private static readonly double BulkTokensPerSecond =
        BulkBurstCapacity / BulkReplenishmentPeriod.TotalSeconds;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private readonly Dictionary<Guid, DeviceBuckets> buckets = new();
    private DateTimeOffset nextCleanupUtc = DateTimeOffset.MinValue;

    public AgentRateLimitDecision TryAcquire(Guid agentId, DateTimeOffset now) =>
        TryAcquire(agentId, now, AgentTrafficClass.Standard);

    public AgentRateLimitDecision TryAcquire(
        Guid agentId,
        DateTimeOffset now,
        AgentTrafficClass trafficClass)
    {
        if (agentId == Guid.Empty)
            throw new ArgumentException("A verified Agent device ID is required.", nameof(agentId));

        lock (gate)
        {
            RemoveInactiveBuckets(now);

            if (!buckets.TryGetValue(agentId, out var deviceBuckets))
            {
                deviceBuckets = new DeviceBuckets(now);
                buckets.Add(agentId, deviceBuckets);
            }

            deviceBuckets.LastSeenUtc = now;
            deviceBuckets.Shared.Refill(now);
            deviceBuckets.PriorityReserve.Refill(now);
            deviceBuckets.Bulk.Refill(now);

            if (trafficClass == AgentTrafficClass.Priority)
            {
                // Priority work uses the shared allowance first. Only after the
                // shared bucket is exhausted does it use its independent reserve.
                if (deviceBuckets.Shared.TryConsume())
                    return new AgentRateLimitDecision(true, TimeSpan.Zero);

                if (deviceBuckets.PriorityReserve.TryConsume())
                    return new AgentRateLimitDecision(true, TimeSpan.Zero);

                return new AgentRateLimitDecision(
                    false,
                    Max(
                        deviceBuckets.Shared.TimeUntilOneToken(),
                        deviceBuckets.PriorityReserve.TimeUntilOneToken()));
            }

            if (trafficClass == AgentTrafficClass.BulkReconciliation)
            {
                // Bulk work must have capacity in both buckets. Neither bucket
                // is consumed when the other would reject the request.
                if (deviceBuckets.Shared.Tokens < 1 || deviceBuckets.Bulk.Tokens < 1)
                {
                    return new AgentRateLimitDecision(
                        false,
                        Max(
                            deviceBuckets.Shared.TimeUntilOneToken(),
                            deviceBuckets.Bulk.TimeUntilOneToken()));
                }

                deviceBuckets.Shared.Consume();
                deviceBuckets.Bulk.Consume();
                return new AgentRateLimitDecision(true, TimeSpan.Zero);
            }

            if (!deviceBuckets.Shared.TryConsume())
            {
                return new AgentRateLimitDecision(
                    false,
                    deviceBuckets.Shared.TimeUntilOneToken());
            }

            return new AgentRateLimitDecision(true, TimeSpan.Zero);
        }
    }

    public static AgentTrafficClass ClassifyTraffic(string? method, string? path)
    {
        var verb = (method ?? string.Empty).Trim().ToUpperInvariant();
        var route = (path ?? string.Empty).TrimEnd('/').ToLowerInvariant();

        if (route.EndsWith("/heartbeat", StringComparison.Ordinal) ||
            route.EndsWith("/printers/sync", StringComparison.Ordinal) ||
            route.EndsWith("/jobs/claim", StringComparison.Ordinal) ||
            route.EndsWith("/jobs/print-claim", StringComparison.Ordinal) ||
            route.EndsWith("/received", StringComparison.Ordinal) ||
            route.EndsWith("/spooler-accepted", StringComparison.Ordinal) ||
            route.EndsWith("/spooler-status", StringComparison.Ordinal) ||
            route.EndsWith("/pdf-exported", StringComparison.Ordinal) ||
            route.EndsWith("/output-unconfirmed", StringComparison.Ordinal) ||
            route.EndsWith("/print-blocked", StringComparison.Ordinal) ||
            route.EndsWith("/private-test-output", StringComparison.Ordinal) ||
            (route.Contains("/private-relays/", StringComparison.Ordinal) &&
             (route.EndsWith("/complete", StringComparison.Ordinal) ||
              route.EndsWith("/reject", StringComparison.Ordinal) ||
              route.EndsWith("/stage", StringComparison.Ordinal))))
            return AgentTrafficClass.Priority;

        if ((verb == "GET" &&
             route.Contains("/local-uploads/", StringComparison.Ordinal) &&
             route.EndsWith("/status", StringComparison.Ordinal)) ||
            (verb == "POST" && route.EndsWith("/local-uploads/status-batch", StringComparison.Ordinal)))
            return AgentTrafficClass.BulkReconciliation;

        if (verb == "GET" && route.EndsWith("/private-relays/pending", StringComparison.Ordinal))
            return AgentTrafficClass.BulkReconciliation;

        return AgentTrafficClass.Standard;
    }

    private void RemoveInactiveBuckets(DateTimeOffset now)
    {
        if (now < nextCleanupUtc) return;

        var inactiveBefore = now - InactiveEntryRetention;
        foreach (var agentId in buckets
                     .Where(pair => pair.Value.LastSeenUtc < inactiveBefore)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            buckets.Remove(agentId);
        }

        nextCleanupUtc = now + CleanupInterval;
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) =>
        first >= second ? first : second;

    private sealed class DeviceBuckets(DateTimeOffset createdUtc)
    {
        public DateTimeOffset LastSeenUtc { get; set; } = createdUtc;
        public TokenBucket Shared { get; } = new(BurstCapacity, TokensPerSecond, createdUtc);
        public TokenBucket PriorityReserve { get; } = new(PriorityReserveCapacity, TokensPerSecond, createdUtc);
        public TokenBucket Bulk { get; } = new(BulkBurstCapacity, BulkTokensPerSecond, createdUtc);
    }

    private sealed class TokenBucket
    {
        private readonly double capacity;
        private readonly double tokensPerSecond;
        public double Tokens { get; private set; }
        private DateTimeOffset LastRefillUtc { get; set; }

        public TokenBucket(double capacity, double tokensPerSecond, DateTimeOffset createdUtc)
        {
            this.capacity = capacity;
            this.tokensPerSecond = tokensPerSecond;
            Tokens = capacity;
            LastRefillUtc = createdUtc;
        }

        public void Refill(DateTimeOffset now)
        {
            if (now <= LastRefillUtc) return;
            var elapsedSeconds = (now - LastRefillUtc).TotalSeconds;
            Tokens = Math.Min(capacity, Tokens + elapsedSeconds * tokensPerSecond);
            LastRefillUtc = now;
        }

        public bool TryConsume()
        {
            if (Tokens < 1) return false;
            Consume();
            return true;
        }

        public void Consume() => Tokens -= 1;

        public TimeSpan TimeUntilOneToken()
        {
            if (Tokens >= 1) return TimeSpan.Zero;
            var seconds = (1 - Tokens) / tokensPerSecond;
            return TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(seconds * 1000)));
        }
    }
}

public readonly record struct AgentRateLimitDecision(bool IsAllowed, TimeSpan RetryAfter);
