namespace SmartPrint.Api.Domain;

public static class ShopProfileRules
{
    public static bool IsHexColor(string value) =>
        value is { Length: 7 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
}
