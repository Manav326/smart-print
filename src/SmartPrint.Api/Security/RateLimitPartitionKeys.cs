using Microsoft.AspNetCore.Http;

namespace SmartPrint.Api.Security;

public static class RateLimitPartitionKeys
{
    public static string GetRateLimitPartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static string GetSessionRateLimitPartitionKey(HttpContext context) =>
        GetRateLimitPartitionKey(context);

    public static string GetAgentRateLimitPartitionKey(HttpContext context) =>
        GetRateLimitPartitionKey(context);
}
