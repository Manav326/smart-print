using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.PlatformOperationalSettings.Endpoints;
using SmartPrint.Api.Modules.PlatformOperationalSettings.Services;

namespace SmartPrint.Api.Modules.PlatformOperationalSettings.Registration;

public static class PlatformOperationalSettingsModule
{
    public static IServiceCollection AddPlatformOperationalSettingsModule(this IServiceCollection services)
    {
        services.AddScoped<PlatformOperationalSettingsService>();
        return services;
    }

    public static RouteGroupBuilder MapPlatformOperationalSettingsModule(this RouteGroupBuilder platform)
    {
        PlatformOperationalSettingsEndpoints.MapPlatformOperationalSettingsModule(platform);
        return platform;
    }
}
