using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using SmartPrint.Api.Modules.PlatformAdvertising.Endpoints;

namespace SmartPrint.Api.Modules.PlatformAdvertising.Registration;

public static class PlatformAdvertisingModule
{
    public static RouteGroupBuilder MapPlatformAdvertisingQueriesModule(this RouteGroupBuilder platform)
    {
        PlatformAdvertisingQueryEndpoints.MapPlatformAdvertisingQueries(platform);
        return platform;
    }
    public static WebApplication MapPublicAdvertisingQueriesModule(this WebApplication app)
    {
        PlatformAdvertisingQueryEndpoints.MapPublicAdvertisingQueries(app);
        return app;
    }
}
