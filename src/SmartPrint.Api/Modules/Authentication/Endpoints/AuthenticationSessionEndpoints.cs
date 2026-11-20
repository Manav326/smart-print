using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Modules.Authentication.Services;

namespace SmartPrint.Api.Modules.Authentication.Endpoints;

/// <summary>
/// HTTP adapters for refresh-token rotation and logout revocation.
/// </summary>
public static class AuthenticationSessionEndpoints
{
    public static RouteGroupBuilder MapAuthenticationSessionEndpoints(this RouteGroupBuilder auth)
    {
        auth.MapPost("/refresh", async (
            RefreshRequest request,
            AuthenticationSessionService sessions,
            CancellationToken ct) =>
        {
            var pair = await sessions.RefreshAsync(request.RefreshToken, ct);
            return pair is null
                ? Results.Unauthorized()
                : Results.Ok(new TokenResponse("Bearer", pair.AccessToken, pair.RefreshToken, pair.AccessTokenExpiresUtc));
        }).AllowAnonymous();

        auth.MapPost("/logout", async (
            RefreshRequest request,
            AuthenticationSessionService sessions,
            CancellationToken ct) =>
        {
            await sessions.RevokeAsync(request.RefreshToken, ct);
            return Results.NoContent();
        }).AllowAnonymous();

        return auth;
    }
}
