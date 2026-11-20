using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.PlatformDataTransfer.Endpoints;

namespace SmartPrint.Api.Modules.PlatformDataTransfer.Registration;

/// <summary>
/// Composition-root entry point for platform data-transfer routes.
/// </summary>
public static class PlatformDataTransferModule
{
    public static RouteGroupBuilder MapPlatformDataTransferModule(this RouteGroupBuilder platform)
    {
        PlatformDataTransferEndpoints.MapPlatformDataTransferEndpoints(platform);
        return platform;
    }
}
