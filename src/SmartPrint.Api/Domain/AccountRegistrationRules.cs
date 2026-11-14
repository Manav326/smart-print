using System.Net.Mail;
using SmartPrint.Api.Contracts;

namespace SmartPrint.Api.Domain;

public static class AccountRegistrationRules
{
    public static bool TryNormalizeShop(
        RegisterShopRequest request,
        out string businessName,
        out string ownerName,
        out string email)
    {
        ArgumentNullException.ThrowIfNull(request);
        businessName = request.BusinessName?.Trim() ?? string.Empty;
        ownerName = request.OwnerName?.Trim() ?? string.Empty;
        email = request.Email?.Trim() ?? string.Empty;

        return businessName.Length is > 0 and <= 180 &&
               ownerName.Length is > 0 and <= 160 &&
               email.Length is > 0 and <= 256 &&
               MailAddress.TryCreate(email, out _) &&
               !string.IsNullOrWhiteSpace(request.Password);
    }

    public static bool TryNormalizeCustomer(
        RegisterCustomerRequest request,
        out string displayName,
        out string email)
    {
        ArgumentNullException.ThrowIfNull(request);
        displayName = request.DisplayName?.Trim() ?? string.Empty;
        email = request.Email?.Trim() ?? string.Empty;

        return displayName.Length is > 0 and <= 160 &&
               email.Length is > 0 and <= 256 &&
               MailAddress.TryCreate(email, out _) &&
               !string.IsNullOrWhiteSpace(request.Password);
    }
}
