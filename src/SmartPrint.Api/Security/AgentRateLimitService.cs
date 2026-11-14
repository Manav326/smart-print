namespace SmartPrint.Api.Security;

/// <summary>
/// Per-device token bucket for Agent API traffic. Buckets are created only after
/// credentials are verified and inactive entries are pruned, so request headers
/// cannot create unbounded rate-limit partitions.
/// </summary>
public sealed class AgentRateLimitService
{
    public const string AuthenticatedAgentItemKey = "SmartPrint.AuthenticatedAgentDevice";

    public const int BurstCapacity = 120;
    public const int TokensPerReplenishment = 60;
    public static readonly TimeSpan ReplenishmentPeriod = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan InactiveEntryRetention = TimeSpan.FromMinutes(2);

    private static readonly double TokensPerSecond =
        TokensPerReplenishment / ReplenishmentPeriod.TotalSeconds;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private readonly Dictionary<Guid, Bucket> buckets = new();
    private DateTimeOffset nextCleanupUtc = DateTimeOffset.MinValue;

    public AgentRateLimitDecision TryAcquire(Guid agentId, DateTimeOffset now)
    {
        if (agentId == Guid.Empty)
            throw new ArgumentException("A verified Agent device ID is required.", nameof(agentId));

        lock (gate)
        {
            RemoveInactiveBuckets(now);

            if (!buckets.TryGetValue(agentId, out var bucket))
            {
                bucket = new Bucket(now);
                buckets.Add(agentId, bucket);
            }

            if (now > bucket.LastSeenUtc)
                bucket.LastSeenUtc = now;

            var elapsedSeconds = Math.Max(0, (now - bucket.LastRefillUtc).TotalSeconds);
            if (elapsedSeconds > 0)
            {
                bucket.Tokens = Math.Min(
                    BurstCapacity,
                    bucket.Tokens + elapsedSeconds * TokensPerSecond);
                bucket.LastRefillUtc = now;
            }

            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                return new AgentRateLimitDecision(true, TimeSpan.Zero);
            }

            var retryAfter = TimeSpan.FromSeconds((1 - bucket.Tokens) / TokensPerSecond);
            return new AgentRateLimitDecision(
                false,
                TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(retryAfter.TotalMilliseconds))));
        }
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

    private sealed class Bucket(DateTimeOffset createdUtc)
    {
        public double Tokens { get; set; } = BurstCapacity;
        public DateTimeOffset LastRefillUtc { get; set; } = createdUtc;
        public DateTimeOffset LastSeenUtc { get; set; } = createdUtc;
    }
}

public readonly record struct AgentRateLimitDecision(bool IsAllowed, TimeSpan RetryAfter);
