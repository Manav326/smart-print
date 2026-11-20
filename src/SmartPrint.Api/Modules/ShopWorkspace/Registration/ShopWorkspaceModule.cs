using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.ShopWorkspace.Endpoints;

namespace SmartPrint.Api.Modules.ShopWorkspace.Registration;

/// <summary>
/// Explicit route-registration entry point for shop workspace queries.
/// </summary>
public static class ShopWorkspaceModule
{
    public static RouteGroupBuilder MapShopWorkspaceQueriesModule(this RouteGroupBuilder shop)
    {
        ShopWorkspaceQueryEndpoints.MapShopWorkspaceQueries(shop);
        return shop;
    }
}
