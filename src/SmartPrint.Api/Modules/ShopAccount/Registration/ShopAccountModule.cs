using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.ShopAccount.Endpoints;
using SmartPrint.Api.Modules.ShopAccount.Services;

namespace SmartPrint.Api.Modules.ShopAccount.Registration;

/// <summary>
/// DI and route registration for the shop account capability.
/// </summary>
public static class ShopAccountModule
{
    public static IServiceCollection AddShopAccountModule(this IServiceCollection services)
    {
        services.AddScoped<ShopAccountService>();
        return services;
    }

    public static RouteGroupBuilder MapShopAccountModule(this RouteGroupBuilder shop)
    {
        ShopAccountEndpoints.MapShopAccountEndpoints(shop);
        return shop;
    }
}
