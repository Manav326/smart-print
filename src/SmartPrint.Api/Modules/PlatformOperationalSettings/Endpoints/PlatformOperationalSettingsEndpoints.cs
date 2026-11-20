using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Modules.PlatformOperationalSettings.Contracts;
using SmartPrint.Api.Modules.PlatformOperationalSettings.Services;

namespace SmartPrint.Api.Modules.PlatformOperationalSettings.Endpoints;

/// <summary>
/// HTTP adapters for platform operational settings and their audit history.
/// Persistence and update transactions are owned by the application service.
/// </summary>
public static class PlatformOperationalSettingsEndpoints
{
    public static RouteGroupBuilder MapPlatformOperationalSettingsModule(this RouteGroupBuilder platform)
    {
        platform.MapGet("/settings/operations", async (
            PlatformOperationalSettingsService service,
            CancellationToken ct) =>
        {
            var settings = await service.GetAsync(ct);
            return settings is null
                ? Results.Problem(statusCode: 503, title: "Platform operational settings are not initialized.")
                : Results.Ok(settings);
        });

        platform.MapGet("/settings/operations/history", async (
            PlatformOperationalSettingsService service,
            CancellationToken ct) =>
            Results.Ok(await service.GetHistoryAsync(ct)));

        platform.MapPut("/settings/operations", async (
            PlatformOperationalSettingsRequest request,
            ClaimsPrincipal principal,
            PlatformOperationalSettingsService service,
            CancellationToken ct) =>
        {
            var actorValue = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
                principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(actorValue, out var actorId)) return Results.Forbid();

            var result = await service.UpdateAsync(request, actorId, ct);
            if (result.ValidationErrors.Count != 0)
                return Results.ValidationProblem(result.ValidationErrors);
            if (result.SettingsNotInitialized)
                return Results.Problem(statusCode: 503, title: "Platform operational settings are not initialized.");

            return Results.Ok(result.Response);
        });

        return platform;
    }
}
