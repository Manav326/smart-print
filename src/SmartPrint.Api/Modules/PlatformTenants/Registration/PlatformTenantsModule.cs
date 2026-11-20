using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.PlatformTenants.Endpoints;
using SmartPrint.Api.Modules.PlatformTenants.Services;

namespace SmartPrint.Api.Modules.PlatformTenants.Registration;

/// <summary>
/// DI and route registration for platform tenant lifecycle management.
/// </summary>
public static class PlatformTenantsModule
{
    public static IServiceCollection AddPlatformTenantsModule(this IServiceCollection services)
    {
        services.AddScoped<PlatformTenantStatusService>();
        return services;
    }

    public static RouteGroupBuilder MapPlatformTenantStatusModule(this RouteGroupBuilder platform)
    {
        PlatformTenantStatusEndpoints.MapPlatformTenantStatusEndpoints(platform);
        return platform;
    }
}
