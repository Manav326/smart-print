namespace SmartPrint.Api.Domain;

public static class ShopPaymentReviewRules
{
    public static bool CanVerify(string? method, string? status) =>
        method switch
        {
            "UPI" => status is "AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED",
            "CASH" => status == "CASH_AWAITING_PAYMENT",
            _ => false
        };

    public static bool CanReject(string? status) =>
        status is "AWAITING_CUSTOMER_PAYMENT" or "CUSTOMER_REPORTED" or "CASH_AWAITING_PAYMENT";

    public static bool MustPreserveRejectedJobStatus(string? jobStatus) =>
        jobStatus is "REJECTED" or "REFUNDED";
}
