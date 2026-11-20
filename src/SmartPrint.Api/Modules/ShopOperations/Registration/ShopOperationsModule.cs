using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.ShopOperations.Endpoints;

namespace SmartPrint.Api.Modules.ShopOperations.Registration;

/// <summary>
/// Explicit route registration for shop customer and operations queries.
/// </summary>
public static class ShopOperationsModule
{
    public static RouteGroupBuilder MapShopOperationsQueriesModule(this RouteGroupBuilder shop)
    {
        ShopOperationsQueryEndpoints.MapShopOperationsQueries(shop);
        return shop;
    }
}
