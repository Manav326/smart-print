using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.ShopProfile.Endpoints;
using SmartPrint.Api.Modules.ShopProfile.Services;

namespace SmartPrint.Api.Modules.ShopProfile.Registration;

/// <summary>
/// DI and route registration for the shop profile capability.
/// </summary>
public static class ShopProfileModule
{
    public static IServiceCollection AddShopProfileModule(this IServiceCollection services)
    {
        services.AddScoped<ShopProfileService>();
        return services;
    }

    public static RouteGroupBuilder MapShopProfileModule(this RouteGroupBuilder shop)
    {
        ShopProfileEndpoints.MapShopProfileEndpoints(shop);
        return shop;
    }
}
