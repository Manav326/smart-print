using System.Net;
using Microsoft.AspNetCore.Http;
using SmartPrint.Api.Security;

namespace SmartPrint.Api.Tests;

public sealed class AgentRateLimitPartitionTests
{
    [Fact]
    public void Different_agents_behind_same_proxy_receive_separate_partitions()
    {
        var first = Context("10.0.0.5", "a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11");
        var second = Context("10.0.0.5", "a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a12");

        Assert.NotEqual(
            AgentRateLimitPartition.GetPartitionKey(first),
            AgentRateLimitPartition.GetPartitionKey(second));
    }

    [Fact]
    public void Same_agent_and_proxy_receive_a_stable_partition()
    {
        var first = Context("10.0.0.5", "a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11");
        var second = Context("10.0.0.5", "a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11");

        Assert.Equal(
            AgentRateLimitPartition.GetPartitionKey(first),
            AgentRateLimitPartition.GetPartitionKey(second));
    }

    [Fact]
    public void Missing_or_invalid_agent_identity_falls_back_to_the_proxy_partition()
    {
        var missing = Context("10.0.0.5", null);
        var malformed = Context("10.0.0.5", "not-a-guid");

        Assert.Equal(
            AgentRateLimitPartition.GetPartitionKey(missing),
            AgentRateLimitPartition.GetPartitionKey(malformed));
    }

    private static DefaultHttpContext Context(string ipAddress, string? agentId)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        if (agentId is not null)
            context.Request.Headers["X-Print-Agent-Id"] = agentId;
        return context;
    }
}