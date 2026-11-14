namespace SmartPrint.Api.Domain;

public static class ShopReportRules
{
    public static bool IsValidPeriod(int days) => days is 7 or 30 or 90;
}
