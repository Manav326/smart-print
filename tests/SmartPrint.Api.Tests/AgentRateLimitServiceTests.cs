using System.Collections.Concurrent;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class AgentRateLimitServiceTests
{
    [Fact]
    public void EachVerifiedAgentReceivesItsOwnBurstAllowance()
    {
        var service = new AgentRateLimitService();
        var now = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        for (var i = 0; i < AgentRateLimitService.BurstCapacity; i++)
            Assert.True(service.TryAcquire(first, now).IsAllowed);

        var firstBlocked = service.TryAcquire(first, now);
        Assert.False(firstBlocked.IsAllowed);
        Assert.True(firstBlocked.RetryAfter > TimeSpan.Zero);

        Assert.True(service.TryAcquire(second, now).IsAllowed);
    }

    [Fact]
    public void TokenBucketReplenishesAtTheConfiguredRate()
    {
        var service = new AgentRateLimitService();
        var agentId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-14T10:00:00Z");

        for (var i = 0; i < AgentRateLimitService.BurstCapacity; i++)
            Assert.True(service.TryAcquire(agentId, now).IsAllowed);
        Assert.False(service.TryAcquire(agentId, now).IsAllowed);

        var afterOneSecond = now.AddSeconds(1);
        Assert.True(service.TryAcquire(agentId, afterOneSecond).IsAllowed);
        Assert.True(service.TryAcquire(agentId, afterOneSecond).IsAllowed);
        Assert.False(service.TryAcquire(agentId, afterOneSecond).IsAllowed);
    }

    [Fact]
    public void ConcurrentRequestsCannotExceedTheDeviceBurstAllowance()
    {
        var service = new AgentRateLimitService();
        var agentId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var results = new ConcurrentBag<bool>();

        Parallel.For(0, 120, _ =>
            results.Add(service.TryAcquire(agentId, now).IsAllowed));

        Assert.Equal(AgentRateLimitService.BurstCapacity, results.Count(allowed => allowed));
        Assert.Equal(120 - AgentRateLimitService.BurstCapacity, results.Count(allowed => !allowed));
    }

    [Fact]
    public void EmptyDeviceIdentityIsRejected()
    {
        var service = new AgentRateLimitService();

        Assert.Throws<ArgumentException>(() =>
            service.TryAcquire(Guid.Empty, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void InactiveAgentBucketsAreEligibleForCleanup()
    {
        var service = new AgentRateLimitService();
        var agentId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-14T10:00:00Z");

        Assert.True(service.TryAcquire(agentId, now).IsAllowed);

        var afterIdle = now + AgentRateLimitService.InactiveEntryRetention + TimeSpan.FromMinutes(1);
        for (var i = 0; i < AgentRateLimitService.BurstCapacity; i++)
            Assert.True(service.TryAcquire(agentId, afterIdle).IsAllowed);
    }
}
