using System.Text.Json.Serialization;

namespace SmartPrint.Api.Modules.Authentication.Contracts;

/// <summary>
/// Stable token response returned by the Google sign-in endpoints.
/// Explicit JSON names preserve the existing public response contract.
/// </summary>
public sealed record GoogleAuthSessionResponse(
    [property: JsonPropertyName("tokenType")] string TokenType,
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("accessTokenExpiresUtc")] DateTimeOffset AccessTokenExpiresUtc);
