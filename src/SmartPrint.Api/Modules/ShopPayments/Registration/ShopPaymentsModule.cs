using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.ShopPayments.Endpoints;

namespace SmartPrint.Api.Modules.ShopPayments.Registration;

/// <summary>
/// Explicit registration for shop financial read queries.
/// </summary>
public static class ShopPaymentsModule
{
    public static RouteGroupBuilder MapShopPaymentQueriesModule(this RouteGroupBuilder shop)
    {
        ShopPaymentQueryEndpoints.MapShopPaymentQueries(shop);
        return shop;
    }
}
