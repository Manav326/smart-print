using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Modules.Authentication.Abstractions;

namespace SmartPrint.Api.Modules.Authentication.Infrastructure;

/// <summary>
/// ASP.NET Identity adapter used by authentication-session workflows.
/// </summary>
public sealed class IdentityAuthenticationUserAccess(UserManager<AppUser> userManager) : IAuthenticationUserAccess
{
    public Task<AppUser?> FindByIdAsync(Guid userId) =>
        userManager.FindByIdAsync(userId.ToString());

    public async Task<IReadOnlyList<Claim>> GetClaimsAsync(AppUser user) =>
        (await userManager.GetClaimsAsync(user)).ToArray();
}
