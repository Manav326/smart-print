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
        for (var i = 0; i < AgentRateLimitService.TokensPerReplenishment /
             AgentRateLimitService.ReplenishmentPeriod.TotalSeconds; i++)
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
    public void PriorityTrafficRetainsReservedCapacityAfterStandardTrafficExhaustsTheSharedBucket()
    {
        var service = new AgentRateLimitService();
        var agentId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-20T10:00:00Z");

        for (var i = 0; i < AgentRateLimitService.BurstCapacity; i++)
            Assert.True(service.TryAcquire(agentId, now, AgentTrafficClass.Standard).IsAllowed);

        for (var i = 0; i < AgentRateLimitService.PriorityReserveCapacity; i++)
            Assert.True(service.TryAcquire(agentId, now, AgentTrafficClass.Priority).IsAllowed);

        var blocked = service.TryAcquire(agentId, now, AgentTrafficClass.Priority);
        Assert.False(blocked.IsAllowed);
        Assert.True(blocked.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void BulkReconciliationCannotConsumePriorityReserve()
    {
        var service = new AgentRateLimitService();
        var agentId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-20T10:00:00Z");

        for (var i = 0; i < AgentRateLimitService.BulkBurstCapacity; i++)
            Assert.True(service.TryAcquire(agentId, now, AgentTrafficClass.BulkReconciliation).IsAllowed);

        var bulkBlocked = service.TryAcquire(agentId, now, AgentTrafficClass.BulkReconciliation);
        Assert.False(bulkBlocked.IsAllowed);
        Assert.True(bulkBlocked.RetryAfter > TimeSpan.Zero);

        Assert.True(service.TryAcquire(agentId, now, AgentTrafficClass.Priority).IsAllowed);
        Assert.True(service.TryAcquire(agentId, now, AgentTrafficClass.Standard).IsAllowed);
    }

    [Theory]
    [InlineData("POST", "/api/v1/agent/heartbeat", AgentTrafficClass.Priority)]
    [InlineData("POST", "/api/v1/agent/printers/sync", AgentTrafficClass.Priority)]
    [InlineData("POST", "/api/v1/agent/jobs/print-claim", AgentTrafficClass.Priority)]
    [InlineData("GET", "/api/v1/agent/local-uploads/00000000-0000-0000-0000-000000000001/status", AgentTrafficClass.BulkReconciliation)]
    [InlineData("POST", "/api/v1/agent/local-uploads/status-batch", AgentTrafficClass.BulkReconciliation)]
    [InlineData("GET", "/api/v1/agent/private-relays/pending", AgentTrafficClass.BulkReconciliation)]
    [InlineData("POST", "/api/v1/agent/jobs/claim", AgentTrafficClass.Priority)]
    [InlineData("POST", "/api/v1/agent/jobs/00000000-0000-0000-0000-000000000001/received", AgentTrafficClass.Priority)]
    [InlineData("POST", "/api/v1/agent/private-relays/00000000-0000-0000-0000-000000000001/complete", AgentTrafficClass.Priority)]
    [InlineData("POST", "/api/v1/agent/diagnostics", AgentTrafficClass.Standard)]
    public void ClassifiesAgentTrafficByOperationalPriority(
        string method, string path, AgentTrafficClass expected)
    {
        Assert.Equal(expected, AgentRateLimitService.ClassifyTraffic(method, path));
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
