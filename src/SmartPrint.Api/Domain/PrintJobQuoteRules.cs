namespace SmartPrint.Api.Domain;

public static class PrintJobQuoteRules
{
    public static bool CanCreateQuote(string? status) =>
        status is "AWAITING_CONFIGURATION" or "QUOTE_EXPIRED" or "Uploaded" or "UPLOADED";

    public static string ResolveStatusAfterQuote(
        string? previousStatus,
        bool isExpiredQuoteRenewal,
        bool pricingCardChanged,
        bool instantPaymentEnabled)
    {
        if (!isExpiredQuoteRenewal)
            return instantPaymentEnabled ? "AWAITING_PAYMENT" : "AWAITING_CUSTOMER_APPROVAL";

        // A changed shop pricing card requires fresh customer acceptance.
        // Its version is authoritative; a calculated amount delta is not.
        if (pricingCardChanged)
            return "AWAITING_CUSTOMER_APPROVAL";

        return previousStatus switch
        {
            "AWAITING_CUSTOMER_APPROVAL" => "AWAITING_CUSTOMER_APPROVAL",
            "AWAITING_SHOP_APPROVAL" => "AWAITING_SHOP_APPROVAL",
            "AWAITING_PAYMENT" => "AWAITING_PAYMENT",
            "PAYMENT_REJECTED" => instantPaymentEnabled ? "AWAITING_PAYMENT" : "AWAITING_CUSTOMER_APPROVAL",
            _ => instantPaymentEnabled ? "AWAITING_PAYMENT" : "AWAITING_CUSTOMER_APPROVAL"
        };
    }
}
