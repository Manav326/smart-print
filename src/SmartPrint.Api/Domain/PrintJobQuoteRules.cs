namespace SmartPrint.Api.Domain;

public static class PrintJobQuoteRules
{
    public static bool CanCreateQuote(string? status) =>
        status is "AWAITING_CONFIGURATION" or "QUOTE_EXPIRED" or "Uploaded" or "UPLOADED";

    public static string ResolveStatusAfterQuote(
        string? previousStatus,
        bool isExpiredQuoteRenewal,
        bool priceChanged,
        bool instantPaymentEnabled)
    {
        if (!isExpiredQuoteRenewal)
            return instantPaymentEnabled ? "AWAITING_PAYMENT" : "AWAITING_CUSTOMER_APPROVAL";

        // A changed price is never silently authorised, even when the shop
        // normally enables instant payment. The customer must accept it again.
        if (priceChanged)
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
