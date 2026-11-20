using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.ShopPricing.Endpoints;
using SmartPrint.Api.Modules.ShopPricing.Services;

namespace SmartPrint.Api.Modules.ShopPricing.Registration;

/// <summary>
/// DI and route registration for the shop pricing capability.
/// </summary>
public static class ShopPricingModule
{
    public static IServiceCollection AddShopPricingModule(this IServiceCollection services)
    {
        services.AddScoped<ShopPricingService>();
        return services;
    }

    public static RouteGroupBuilder MapShopPricingModule(this RouteGroupBuilder shop)
    {
        ShopPricingEndpoints.MapShopPricingEndpoints(shop);
        return shop;
    }
}
