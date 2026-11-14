using System.Text;

namespace SmartPrint.Api.Domain;

public static class TenantSlugPolicy
{
    public static string Create(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "print-shop" : slug[..Math.Min(slug.Length, 60)];
    }
}
