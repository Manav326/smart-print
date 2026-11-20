using Microsoft.AspNetCore.Builder;
using SmartPrint.Api.Modules.Customer.Endpoints;

namespace SmartPrint.Api.Modules.Customer.Registration;

public static class CustomerModule
{
    public static WebApplication MapCustomerModule(this WebApplication app)
    {
        CustomerQueryEndpoints.MapCustomerQueryEndpoints(app);
        return app;
    }
}
