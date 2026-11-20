using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Modules.PlatformAdvertising.Services;

/// <summary>
/// Shared response projection for platform advertising campaign queries and mutations.
/// </summary>
public static class PlatformAdvertisingResponseMapper
{
    public static object ToResponse(
        PlatformAdCampaign campaign,
        PlatformAdCreative? creative,
        long impressions,
        long clicks,
        DateTimeOffset now) => new
    {
        campaign.Id,
        campaign.Name,
        campaign.AdvertiserName,
        campaign.Headline,
        campaign.Body,
        campaign.Terms,
        campaign.CreativeId,
        creativeUrl = creative is null ? null : $"/api/v1/advertising/creatives/{creative.Id}",
        creativeFileName = creative?.FileName,
        campaign.DestinationUrl,
        campaign.Placement,
        campaign.Format,
        status = PlatformAdvertisingRules.DisplayStatus(campaign, now),
        storedStatus = campaign.Status,
        campaign.StartsUtc,
        campaign.EndsUtc,
        campaign.CreatedUtc,
        campaign.UpdatedUtc,
        impressions,
        clicks,
        clickThroughRate = impressions == 0 ? 0 : Math.Round((double)clicks / impressions * 100, 2)
    };
}
