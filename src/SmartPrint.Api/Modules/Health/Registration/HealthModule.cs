using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.Health.Endpoints;

namespace SmartPrint.Api.Modules.Health.Registration;

/// <summary>
/// Composition-root entry point for the health module.
/// </summary>
public static class HealthModule
{
    public static IEndpointRouteBuilder MapHealthModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthEndpoints();
        return endpoints;
    }
}
