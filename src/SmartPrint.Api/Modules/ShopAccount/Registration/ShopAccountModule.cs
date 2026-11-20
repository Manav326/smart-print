using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.ShopAccount.Endpoints;
using SmartPrint.Api.Modules.ShopAccount.Services;
using SmartPrint.Api.Modules.ShopAccount.Abstractions;
using SmartPrint.Api.Modules.ShopAccount.Infrastructure;

namespace SmartPrint.Api.Modules.ShopAccount.Registration;

/// <summary>
/// DI and route registration for the shop account capability.
/// </summary>
public static class ShopAccountModule
{
    public static IServiceCollection AddShopAccountModule(this IServiceCollection services)
    {
        services.AddScoped<IShopAccountIdentity, IdentityShopAccountIdentity>();
        services.AddScoped<ShopAccountService>();
        return services;
    }

    public static RouteGroupBuilder MapShopAccountModule(this RouteGroupBuilder shop)
    {
        ShopAccountEndpoints.MapShopAccountEndpoints(shop);
        return shop;
    }
}
