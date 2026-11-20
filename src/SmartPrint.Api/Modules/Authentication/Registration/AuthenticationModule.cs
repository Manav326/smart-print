using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartPrint.Api.Modules.Authentication.Abstractions;
using SmartPrint.Api.Modules.Authentication.Endpoints;
using SmartPrint.Api.Modules.Authentication.Infrastructure;
using SmartPrint.Api.Modules.Authentication.Services;

namespace SmartPrint.Api.Modules.Authentication.Registration;

/// <summary>
/// Composition-root entry points for authentication dependency registration and routes.
/// </summary>
public static class AuthenticationModule
{
    public static IServiceCollection AddAuthenticationModule(this IServiceCollection services)
    {
        services.AddScoped<GoogleAuthSessionService>();
        services.AddScoped<IAuthenticationUserAccess, IdentityAuthenticationUserAccess>();
        services.AddScoped<AuthenticationSessionService>();
        return services;
    }

    public static RouteGroupBuilder MapGoogleAuthModule(this RouteGroupBuilder auth)
    {
        GoogleAuthEndpoints.MapGoogleAuthEndpoints(auth);
        return auth;
    }

    public static RouteGroupBuilder MapAuthenticationSessionModule(this RouteGroupBuilder auth)
    {
        AuthenticationSessionEndpoints.MapAuthenticationSessionEndpoints(auth);
        return auth;
    }
}
