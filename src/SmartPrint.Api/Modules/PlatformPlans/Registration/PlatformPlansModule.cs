using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.PlatformPlans.Endpoints;

namespace SmartPrint.Api.Modules.PlatformPlans.Registration;

public static class PlatformPlansModule
{
    public static RouteGroupBuilder MapPlatformPlansQueryModule(this RouteGroupBuilder platform)
    {
        PlatformPlanQueryEndpoints.MapPlatformPlanQueries(platform);
        return platform;
    }
}
