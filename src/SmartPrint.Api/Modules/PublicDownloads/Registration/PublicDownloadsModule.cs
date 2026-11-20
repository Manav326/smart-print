using Microsoft.AspNetCore.Builder;
using SmartPrint.Api.Modules.PublicDownloads.Endpoints;

namespace SmartPrint.Api.Modules.PublicDownloads.Registration;

public static class PublicDownloadsModule
{
    public static WebApplication MapPublicDownloadModule(this WebApplication app)
    {
        PublicDownloadEndpoints.MapPublicDownloadEndpoints(app);
        return app;
    }
}
