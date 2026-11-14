using Microsoft.AspNetCore.Http;

namespace SmartPrint.Api.Security;

public static class AgentRateLimitPartition
{
    public static string GetPartitionKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var rawAgentId = context.Request.Headers["X-Print-Agent-Id"].FirstOrDefault();
        return Guid.TryParse(rawAgentId, out var agentId)
            ? $"{remoteAddress}|agent:{agentId:N}"
            : $"{remoteAddress}|agent:unidentified";
    }
}