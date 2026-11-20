using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.ShopDevices.Endpoints;
using SmartPrint.Api.Modules.ShopDevices.Services;

namespace SmartPrint.Api.Modules.ShopDevices.Registration;

/// <summary>
/// DI and route registration for printer and QR-station lifecycle workflows.
/// </summary>
public static class ShopDevicesModule
{
    public static IServiceCollection AddShopDevicesModule(this IServiceCollection services)
    {
        services.AddScoped<ShopDevicesService>();
        return services;
    }

    public static RouteGroupBuilder MapShopDevicesModule(this RouteGroupBuilder shop)
    {
        ShopDevicesEndpoints.MapShopDevicesEndpoints(shop);
        return shop;
    }
}
