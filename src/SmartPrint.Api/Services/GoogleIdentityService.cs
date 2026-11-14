using System.Net.Mail;
using System.Text.Json;
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

        var nonce = ReadNonceClaim(token);
        if (string.IsNullOrWhiteSpace(expectedNonce) ||
            expectedNonce.Length != 64 ||
            nonce.Length != expectedNonce.Length ||
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(nonce),
                System.Text.Encoding.UTF8.GetBytes(expectedNonce)))
        {
            throw new GoogleIdentityException(
                "google_nonce_invalid",
                "This Google sign-in request has expired. Please start again.",
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

    private static string ReadNonceClaim(string signedToken)
    {
        var parts = signedToken.Split('.');
        if (parts.Length != 3) return string.Empty;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("nonce", out var nonce) &&
                   nonce.ValueKind == JsonValueKind.String
                ? nonce.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}

public sealed record VerifiedGoogleIdentity(string Subject, string Email, string DisplayName);

public sealed class GoogleIdentityException(string code, string message, int statusCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
