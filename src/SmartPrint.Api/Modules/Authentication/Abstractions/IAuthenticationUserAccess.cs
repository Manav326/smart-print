using System.Security.Claims;
using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Modules.Authentication.Abstractions;

/// <summary>
/// Identity access required by authentication workflows, without exposing ASP.NET Identity
/// to the application-service layer.
/// </summary>
public interface IAuthenticationUserAccess
{
    Task<AppUser?> FindByIdAsync(Guid userId);
    Task<IReadOnlyList<Claim>> GetClaimsAsync(AppUser user);
}
