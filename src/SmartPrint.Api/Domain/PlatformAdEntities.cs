namespace SmartPrint.Api.Domain;
public sealed class PlatformAdCreative
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class PlatformAdCampaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string AdvertiserName { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Terms { get; set; }
    public Guid CreativeId { get; set; }
    public string DestinationUrl { get; set; } = "";
    public string Placement { get; set; } = "HOME";
    public string Format { get; set; } = "BANNER";
    public string Status { get; set; } = "Draft";
    public DateTimeOffset StartsUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndsUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class PlatformAdEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string EventType { get; set; } = "";
    public string Placement { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class PlatformAdCampaignAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
