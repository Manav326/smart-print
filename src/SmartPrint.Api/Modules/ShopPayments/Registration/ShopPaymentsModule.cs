using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.ShopPayments.Endpoints;
using SmartPrint.Api.Modules.ShopPayments.Services;

namespace SmartPrint.Api.Modules.ShopPayments.Registration;

/// <summary>
/// DI and endpoint registration for shop payment configuration and query capabilities.
/// </summary>
public static class ShopPaymentsModule
{
    public static IServiceCollection AddShopPaymentsModule(this IServiceCollection services)
    {
        services.AddScoped<ShopPaymentSettingsService>();
        return services;
    }

    public static RouteGroupBuilder MapShopPaymentsModule(this RouteGroupBuilder shop)
    {
        ShopPaymentQueryEndpoints.MapShopPaymentQueries(shop);
        ShopPaymentSettingsEndpoints.MapShopPaymentSettingsWrites(shop);
        return shop;
    }
}
