using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.ShopSubscriptionPayments.Endpoints;

namespace SmartPrint.Api.Modules.ShopSubscriptionPayments.Registration;

/// <summary>
/// Composition-root entry point for shop subscription-payment routes.
/// </summary>
public static class ShopSubscriptionPaymentsModule
{
    public static RouteGroupBuilder MapShopSubscriptionPaymentsModule(this RouteGroupBuilder shop)
    {
        ShopSubscriptionPaymentEndpoints.MapShopSubscriptionPaymentEndpoints(shop);
        return shop;
    }
}
