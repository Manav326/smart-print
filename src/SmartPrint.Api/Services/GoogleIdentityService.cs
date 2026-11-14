using System.Net.Mail;
using System.Security.Cryptography;
using Google.Apis.Auth;

namespace SmartPrint.Api.Services;

/// <summary>Verifies provider-signed Google OpenID ID tokens for SmartPrint login.</summary>
public sealed class GoogleIdentityService(IConfiguration configuration)
{
    public const string LoginProvider = "Google";

    public string WebClientId =>
        (configuration["Google:WebClientId"] ??
         configuration["GOOGLE_WEB_CLIENT_ID"] ??
         string.Empty).Trim();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(WebClientId);

    public async Task<VerifiedGoogleIdentity> VerifyAsync(string? credential, string? expectedNonce)
    {
        var clientId = WebClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new GoogleIdentityException(
                "google_sign_in_not_configured",
                "Google sign-in is not configured on this SmartPrint server.",
                StatusCodes.Status503ServiceUnavailable);
        }

        var token = credential?.Trim() ?? string.Empty;
        if (token.Length is < 20 or > 10_000)
        {
            throw new GoogleIdentityException(
                "google_credential_invalid",
                "Google did not return a valid sign-in credential. Please try again.",
                StatusCodes.Status401Unauthorized);
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(
                token,
                new GoogleJsonWebSignature.ValidationSettings { Audience = [clientId] });
        }
        catch (InvalidJwtException)
        {
            throw new GoogleIdentityException(
                "google_credential_invalid",
                "Google sign-in could not be verified. Please try again.",
                StatusCodes.Status401Unauthorized);
        }
        catch (HttpRequestException)
        {
            throw new GoogleIdentityException(
                "google_verification_unavailable",
                "Google verification is temporarily unavailable. Please try again.",
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException)
        {
            throw new GoogleIdentityException(
                "google_verification_unavailable",
                "Google verification timed out. Please try again.",
                StatusCodes.Status503ServiceUnavailable);
        }

        var signedNonce = payload.Nonce ?? string.Empty;
        var expected = expectedNonce ?? string.Empty;
        var signedNonceBytes = System.Text.Encoding.UTF8.GetBytes(signedNonce);
        var expectedNonceBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        if (expected.Length != 64 ||
            signedNonceBytes.Length != expectedNonceBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(signedNonceBytes, expectedNonceBytes))
        {
            throw new GoogleIdentityException(
                "google_nonce_invalid",
                "This Google sign-in request has expired or does not match the signed Google credential. Please try again.",
                StatusCodes.Status401Unauthorized);
        }

        var subject = payload.Subject?.Trim() ?? string.Empty;
        var email = payload.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var name = payload.Name?.Trim() ?? string.Empty;
        var separator = email.IndexOf('@');

        if (string.IsNullOrWhiteSpace(subject) ||
            separator <= 0 ||
            !MailAddress.TryCreate(email, out _) ||
            payload.EmailVerified != true)
        {
            throw new GoogleIdentityException(
                "google_email_not_verified",
                "Google must confirm a verified email address before you can continue.",
                StatusCodes.Status401Unauthorized);
        }

        if (name.Length > 160) name = name[..160];
        if (string.IsNullOrWhiteSpace(name)) name = email[..separator];

        return new VerifiedGoogleIdentity(subject, email, name);
    }

}

public sealed record VerifiedGoogleIdentity(string Subject, string Email, string DisplayName);

public sealed class GoogleIdentityException(string code, string message, int statusCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
