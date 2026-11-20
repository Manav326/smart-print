using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
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
                return PlatformAdvertisingResponseMapper.ToResponse(x,c,views,clicks,now);
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
 
    public static WebApplication MapPublicAdvertisingQueries(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/advertising")
            .AllowAnonymous()
            .RequireRateLimiting("public-api");

        group.MapGet("/active",async(string placement,SmartPrintDbContext db,CancellationToken ct)=>{
            if(!PlatformAdvertisingRules.IsSupportedPlacement(placement))return Results.ValidationProblem(new Dictionary<string,string[]>{{"placement",new[]{"Choose HOME, FILES or SHOP_OVERVIEW."}}});
            var now=DateTimeOffset.UtcNow;
            var candidates=await db.PlatformAdCampaigns.AsNoTracking()
                .Where(x=>x.Placement==placement&&x.Status=="Active"&&x.StartsUtc<=now&&(x.EndsUtc==null||x.EndsUtc>now))
                .OrderBy(x=>x.CreatedUtc).Take(100).ToListAsync(ct);
            if(candidates.Count==0)return Results.NoContent();
            var candidateIds=candidates.Select(x=>x.Id).ToArray();
            var recentCounts=await db.PlatformAdEvents.AsNoTracking()
                .Where(x=>candidateIds.Contains(x.CampaignId)&&x.EventType=="IMPRESSION"&&x.CreatedUtc>=now.AddDays(-7))
                .GroupBy(x=>x.CampaignId).Select(g=>new{CampaignId=g.Key,Count=g.LongCount()}).ToDictionaryAsync(x=>x.CampaignId,x=>x.Count,ct);
            var item=candidates.OrderBy(x=>recentCounts.GetValueOrDefault(x.Id)).ThenBy(x=>x.CreatedUtc).First();
            var creative=await db.PlatformAdCreatives.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==item.CreativeId,ct);
            if(creative is null)return Results.NoContent();
            return Results.Ok(new{item.Id,item.AdvertiserName,item.Headline,item.Body,item.DestinationUrl,item.Placement,item.Format,imageUrl=$"/api/v1/advertising/creatives/{creative.Id}"});
        });

        group.MapGet("/creatives/{id:guid}",async(Guid id,SmartPrintDbContext db,PlatformAdCreativeStorage storage,HttpContext context,CancellationToken ct)=>{
            var now=DateTimeOffset.UtcNow;var creative=await db.PlatformAdCreatives.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);
            if(creative is null)return Results.NotFound();
            var published=await db.PlatformAdCampaigns.AsNoTracking().AnyAsync(x=>x.CreativeId==id&&x.Status=="Active"&&x.StartsUtc<=now&&(x.EndsUtc==null||x.EndsUtc>now),ct);
            if(!published)return Results.NotFound();
            context.Response.Headers["Cache-Control"]="public,max-age=300,stale-while-revalidate=60";context.Response.Headers["X-Content-Type-Options"]="nosniff";
            return Results.File(storage.OpenRead(creative),creative.ContentType,enableRangeProcessing:false);
        });
        return app;
    }
}
