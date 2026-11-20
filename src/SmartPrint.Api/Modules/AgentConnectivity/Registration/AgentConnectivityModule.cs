using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.AgentConnectivity.Endpoints;
using SmartPrint.Api.Modules.AgentConnectivity.Services;

namespace SmartPrint.Api.Modules.AgentConnectivity.Registration;

/// <summary>
/// DI and route registration for authenticated Agent heartbeat and printer inventory workflows.
/// </summary>
public static class AgentConnectivityModule
{
    public static IServiceCollection AddAgentConnectivityModule(this IServiceCollection services)
    {
        services.AddScoped<AgentConnectivityService>();
        return services;
    }

    public static RouteGroupBuilder MapAgentConnectivityModule(
        this RouteGroupBuilder agentApi,
        IReadOnlyList<string> allowedPortalOrigins)
    {
        AgentConnectivityEndpoints.MapAgentConnectivityEndpoints(agentApi, allowedPortalOrigins);
        return agentApi;
    }
}
