using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Storage;
using SmartPrint.Api.Modules.PlatformAdvertising.Services;

namespace SmartPrint.Api.Modules.PlatformAdvertising.Endpoints;

/// <summary>
/// Read-only platform advertising queries. Creative, campaign and event writes
/// remain in their existing application workflow until their service extraction.
/// </summary>
public static class PlatformAdvertisingQueryEndpoints
{
    public static RouteGroupBuilder MapPlatformAdvertisingQueries(this RouteGroupBuilder platform)
    {
        platform.MapGet("/advertising/campaigns", async (SmartPrintDbContext db,CancellationToken ct) =>
        {
            var now=DateTimeOffset.UtcNow;
            var items=await db.PlatformAdCampaigns.AsNoTracking().OrderByDescending(x=>x.UpdatedUtc).Take(250).ToListAsync(ct);
            var ids=items.Select(x=>x.Id).ToArray();
            var events=await db.PlatformAdEvents.AsNoTracking().Where(x=>ids.Contains(x.CampaignId))
                .GroupBy(x=>new{x.CampaignId,x.EventType}).Select(g=>new{g.Key.CampaignId,g.Key.EventType,Count=g.LongCount()}).ToListAsync(ct);
            var creativeIds=items.Select(x=>x.CreativeId).Distinct().ToArray();
            var creatives=await db.PlatformAdCreatives.AsNoTracking().Where(x=>creativeIds.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,ct);
            return Results.Ok(items.Select(x=>{
                creatives.TryGetValue(x.CreativeId,out var c);
                var views=events.Where(e=>e.CampaignId==x.Id&&e.EventType=="IMPRESSION").Select(e=>e.Count).FirstOrDefault();
                var clicks=events.Where(e=>e.CampaignId==x.Id&&e.EventType=="CLICK").Select(e=>e.Count).FirstOrDefault();
                return ToResponse(x,c,views,clicks,now);
            }));
        });

        platform.MapGet("/advertising/creatives/{creativeId:guid}/preview", async (
            Guid creativeId, SmartPrintDbContext db, PlatformAdCreativeStorage storage, CancellationToken ct) =>
        {
            var creative = await db.PlatformAdCreatives.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == creativeId, ct);
            if (creative is null) return Results.NotFound();
            await using var source = storage.OpenRead(creative);
            await using var memory = new MemoryStream();
            await source.CopyToAsync(memory, ct);
            var dataUrl = "data:" + creative.ContentType + ";base64," + Convert.ToBase64String(memory.ToArray());
            return Results.Ok(new { dataUrl, creative.FileName, creative.ContentType });
        });

        platform.MapGet("/advertising/campaigns/{id:guid}/history",async(Guid id,SmartPrintDbContext db,CancellationToken ct)=>{
            if(!await db.PlatformAdCampaigns.AsNoTracking().AnyAsync(x=>x.Id==id,ct))return Results.NotFound();
            var rows=await db.PlatformAdCampaignAudits.AsNoTracking().Where(x=>x.CampaignId==id).OrderByDescending(x=>x.CreatedUtc).Take(100).Select(x=>new{x.Id,x.Action,x.DetailsJson,x.ActorUserId,x.CreatedUtc}).ToListAsync(ct);
            return Results.Ok(rows);
        });
        return platform;
    }
}
