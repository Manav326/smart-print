using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.AgentPrivateDocuments.Endpoints;
using SmartPrint.Api.Modules.AgentPrivateDocuments.Services;
using SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

namespace SmartPrint.Api.Modules.AgentPrivateDocuments.Registration;

/// <summary>
/// Registers the authenticated Agent private-document cleanup workflow.
/// </summary>
public static class AgentPrivateDocumentsModule
{
    public static IServiceCollection AddAgentPrivateDocumentsModule(this IServiceCollection services)
    {
        services.AddScoped<AgentPrivateDocumentPurgeService>();
        services.AddScoped<AgentPrivateRelayService>();
        return services;
    }

    public static RouteGroupBuilder MapAgentPrivateDocumentsModule(this RouteGroupBuilder agentApi)
    {
        AgentPrivateDocumentPurgeEndpoints.MapAgentPrivateDocumentPurgeEndpoints(agentApi);
        AgentPrivateRelayEndpoints.MapAgentPrivateRelayEndpoints(agentApi);
        return agentApi;
    }
}
