using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Modules.Health.Endpoints;

/// <summary>
/// HTTP adapters for liveness, readiness and service discovery.
/// These routes intentionally expose only process and database connectivity state.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new
        {
            service = "qrtoprint-api",
            status = "live",
            version = typeof(HealthEndpoints).Assembly.GetName().Version?.ToString() ?? "0.1.0"
        })).AllowAnonymous();

        endpoints.MapGet("/health/ready", async (SmartPrintDbContext db, CancellationToken ct) =>
        {
            try
            {
                return await db.Database.CanConnectAsync(ct)
                    ? Results.Ok(new { status = "ready", database = "connected" })
                    : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
            }
            catch
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
            }
        }).AllowAnonymous();

        endpoints.MapGet("/", () => Results.Ok(new
        {
            product = "QRtoPrint",
            name = "Smart Print API",
            apiVersion = "v1",
            health = "/health/live"
        })).AllowAnonymous();

        return endpoints;
    }
}
