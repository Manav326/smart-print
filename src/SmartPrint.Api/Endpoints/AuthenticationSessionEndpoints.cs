using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartPrint.Api.Contracts;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Endpoints;

public static class AuthenticationSessionEndpoints
{
    public static RouteGroupBuilder MapAuthenticationSessionEndpoints(this RouteGroupBuilder auth)
    {
        auth.MapPost("/customer-login", async (
            LoginRequest request,
            AuthenticationSessionService sessions,
            CancellationToken ct) =>
        {
            var result = await sessions.CustomerLoginAsync(request, ct);
            return result.Succeeded ? Results.Ok(result.Tokens) : Results.Unauthorized();
        }).AllowAnonymous();

        auth.MapPost("/login", async (
            LoginRequest request,
            AuthenticationSessionService sessions,
            CancellationToken ct) =>
        {
            var result = await sessions.LoginAsync(request, ct);
            return result.Succeeded ? Results.Ok(result.Tokens) : Results.Unauthorized();
        }).AllowAnonymous();

        auth.MapPost("/refresh", async (
            RefreshRequest request,
            AuthenticationSessionService sessions,
            CancellationToken ct) =>
        {
            var result = await sessions.RefreshAsync(request, ct);
            return result.Succeeded ? Results.Ok(result.Tokens) : Results.Unauthorized();
        }).AllowAnonymous();

        auth.MapPost("/logout", async (
            RefreshRequest request,
            AuthenticationSessionService sessions,
            CancellationToken ct) =>
        {
            await sessions.LogoutAsync(request, ct);
            return Results.NoContent();
        }).AllowAnonymous();
        return auth;
    }
}
