namespace SmartPrint.Api.Domain;

public static class BatchPaymentSelectionRules
{
    public static bool IsReviewComplete(string status) => status is
        "REJECTED" or "REFUNDED" or
        "AWAITING_PAYMENT" or "PAYMENT_REJECTED" or
        "AWAITING_CUSTOMER_PAYMENT" or "PAYMENT_REPORTED" or "AWAITING_COUNTER_PAYMENT" or
        "PAID_AUTHORISED" or "LOCAL_QUEUE_CLAIMED" or "LOCAL_QUEUE_READY" or
        "PRINT_AUTHORISED" or "PRINT_SUBMITTING" or "SPOOLER_ACCEPTED" or "PRINTING" or
        "OUTPUT_UNCONFIRMED" or "COMPLETED";

    public static bool IsPayable(string status) =>
        status is "AWAITING_PAYMENT" or "PAYMENT_REJECTED";
}
