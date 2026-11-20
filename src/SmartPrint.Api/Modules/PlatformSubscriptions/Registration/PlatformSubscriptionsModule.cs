using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.PlatformSubscriptions.Endpoints;

namespace SmartPrint.Api.Modules.PlatformSubscriptions.Registration;

public static class PlatformSubscriptionsModule
{
    public static RouteGroupBuilder MapPlatformSubscriptionsQueryModule(this RouteGroupBuilder platform)
    {
        PlatformSubscriptionQueryEndpoints.MapPlatformSubscriptionQueries(platform);
        return platform;
    }
}
