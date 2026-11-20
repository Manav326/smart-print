using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.ShopDevices.Endpoints;
using SmartPrint.Api.Modules.ShopDevices.Services;

namespace SmartPrint.Api.Modules.ShopDevices.Registration;

/// <summary>
/// DI and route registration for shop printers, paired Agents and QR stations.
/// </summary>
public static class ShopDevicesModule
{
    public static IServiceCollection AddShopDevicesModule(this IServiceCollection services)
    {
        services.AddScoped<ShopDevicesService>();
        services.AddScoped<ShopAgentDevicesService>();
        return services;
    }

    public static RouteGroupBuilder MapShopDevicesModule(this RouteGroupBuilder shop)
    {
        ShopDevicesEndpoints.MapShopDevicesEndpoints(shop);
        return shop;
    }

    /// <summary>
    /// Maps the paired-Agent management routes onto the existing shop route group.
    /// </summary>
    public static RouteGroupBuilder MapShopAgentDevicesModule(this RouteGroupBuilder shopAgents)
    {
        ShopAgentDevicesEndpoints.MapShopAgentDevicesEndpoints(shopAgents);
        return shopAgents;
    }
}
