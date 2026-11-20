using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Modules.PublicDownloads.Endpoints;

/// <summary>
/// HTTP adapters for public release availability and download streaming.
/// The existing PublicDownloadService remains the single transfer implementation.
/// </summary>
public static class PublicDownloadEndpoints
{
    public static WebApplication MapPublicDownloadEndpoints(this WebApplication app)
    {
        // Public downloads are proxied by the API and exposed only through the portal's
        // same-origin /downloads routes. No upstream Location header reaches the browser.
        app.MapGet("/downloads/status", async (
            PublicDownloadService downloads,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                return Results.Ok(await downloads.GetStatusAsync(cancellationToken));
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                app.Logger.LogWarning(exception, "Public release availability lookup timed out.");
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Download availability is temporarily unavailable.");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or InvalidOperationException)
            {
                app.Logger.LogWarning(exception, "Public release availability could not be loaded.");
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Download availability is temporarily unavailable.");
            }
        }).RequireRateLimiting("public-api");
        
        async Task HandlePublicDownloadAsync(
            string platformOrAssetName,
            string? version,
            PublicDownloadService downloads,
            HttpContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                if (version is null)
                    await downloads.StreamDownloadAsync(platformOrAssetName, context, cancellationToken);
                else
                    await downloads.StreamVersionedDownloadAsync(version, platformOrAssetName, context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The browser closed or cancelled the download.
            }
            catch (OperationCanceledException exception)
            {
                app.Logger.LogWarning(exception, "Public release lookup timed out for asset {Asset}.", platformOrAssetName);
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers["Cache-Control"] = "no-store";
                }
                else
                {
                    context.Abort();
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or InvalidOperationException)
            {
                app.Logger.LogWarning(exception, "Public download for asset {Asset} failed.", platformOrAssetName);
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers["Cache-Control"] = "no-store";
                }
                else
                {
                    context.Abort();
                }
            }
        }
        
        app.MapGet("/downloads/{platform}", async (
            string platform,
            PublicDownloadService downloads,
            HttpContext context,
            CancellationToken cancellationToken) =>
            await HandlePublicDownloadAsync(platform, null, downloads, context, cancellationToken))
            .RequireRateLimiting("public-api");
        
        // Versioned binary URLs end in .exe or .apk, which Cloudflare caches by default.
        // The path pins the exact immutable release, so an edge HIT can bypass GitHub
        // and the API origin for subsequent customers without caching any user documents.
        app.MapGet("/downloads/{version}/{assetName}", async (
            string version,
            string assetName,
            PublicDownloadService downloads,
            HttpContext context,
            CancellationToken cancellationToken) =>
            await HandlePublicDownloadAsync(assetName, version, downloads, context, cancellationToken))
            .RequireRateLimiting("public-api");
        return app;
    }
}
