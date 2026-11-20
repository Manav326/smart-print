using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.PlatformAdministration.Endpoints;

namespace SmartPrint.Api.Modules.PlatformAdministration.Registration;

public static class PlatformAdministrationModule
{
    public static RouteGroupBuilder MapPlatformAdministrationQueriesModule(this RouteGroupBuilder platform)
    {
        PlatformAdministrationQueryEndpoints.MapPlatformAdministrationQueries(platform);
        return platform;
    }
}
