namespace SmartPrint.Api.Security;

/// <summary>
/// Encodes opaque random values using unpadded Base64URL, without a web-framework dependency.
/// </summary>
public static class Base64UrlTokenEncoder
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
