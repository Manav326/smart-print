namespace SmartPrint.Api.Domain;

public sealed class ShopSubscriptionPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PlanVersionId { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string BillingInterval { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "INR";
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RazorpayOrderId { get; set; } = string.Empty;
    public string? RazorpayPaymentId { get; set; }
    public string Status { get; set; } = "Pending";
    public Guid? AppliedSubscriptionId { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CapturedUtc { get; set; }
}

public sealed class RazorpayWebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? RazorpayOrderId { get; set; }
    public string? RazorpayPaymentId { get; set; }
    public string Status { get; set; } = "Pending";
    public string? LastError { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedUtc { get; set; }
}
