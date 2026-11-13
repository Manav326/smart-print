using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Storage;
namespace SmartPrint.Api.Endpoints;
public static class PlatformAdvertisingEndpoints
{
    public static RouteGroupBuilder MapPlatformAdvertisingEndpoints(this RouteGroupBuilder platform)
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
        platform.MapPost("/advertising/creatives",async(HttpRequest request,ClaimsPrincipal user,SmartPrintDbContext db,PlatformAdCreativeStorage storage,CancellationToken ct)=>{
            var actor=ActorId(user); if(actor is null)return Results.Forbid();
            var name=request.Headers["X-Ad-Creative-File-Name"].FirstOrDefault();
            var size=request.Headers["X-Ad-Creative-File-Size"].FirstOrDefault();
            var length=request.ContentLength??(long.TryParse(size,out var parsed)?parsed:0);
            if(string.IsNullOrWhiteSpace(name)||length<=0)return Results.ValidationProblem(new Dictionary<string,string[]>{{"file",new[]{"Image name and upload length are required."}}});
            StoredAdCreative saved;
            try{saved=await storage.StoreAsync(Guid.NewGuid(),name,request.ContentType?.Split(';',2)[0]??"",request.Body,length,ct);}
            catch(InvalidDataException ex){return Results.ValidationProblem(new Dictionary<string,string[]>{{"file",new[]{ex.Message}}});}
            var creative=new PlatformAdCreative{Id=saved.Id,FileName=saved.FileName,ContentType=saved.ContentType,StorageKey=saved.StorageKey,SizeBytes=saved.SizeBytes,CreatedByUserId=actor,CreatedUtc=DateTimeOffset.UtcNow};
            try{db.PlatformAdCreatives.Add(creative);await db.SaveChangesAsync(ct);}catch{await storage.DeleteAsync(creative);throw;}
            return Results.Created($"/api/v1/advertising/creatives/{creative.Id}",new{creative.Id,creative.FileName,creative.ContentType,creative.SizeBytes,imageUrl=$"/api/v1/advertising/creatives/{creative.Id}"});
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

        platform.MapPost("/advertising/campaigns",async(PlatformAdCampaignRequest request,ClaimsPrincipal user,SmartPrintDbContext db,CancellationToken ct)=>{
            var actor=ActorId(user);if(actor is null)return Results.Forbid();
            var errors=await Validate(request,db,ct);if(errors.Count>0)return Results.ValidationProblem(errors);
            var now=DateTimeOffset.UtcNow;
            var item=new PlatformAdCampaign{Name=request.Name!.Trim(),AdvertiserName=request.AdvertiserName!.Trim(),Headline=request.Headline!.Trim(),Body=request.Body!.Trim(),Terms=Optional(request.Terms),CreativeId=request.CreativeId,DestinationUrl=request.DestinationUrl!.Trim(),Placement=request.Placement!,Format=request.Format!,StartsUtc=request.StartsUtc,EndsUtc=request.EndsUtc,Status="Draft",CreatedByUserId=actor,UpdatedByUserId=actor,CreatedUtc=now,UpdatedUtc=now};
            db.PlatformAdCampaigns.Add(item);Audit(db,item,actor.Value,"CREATED",new{after=item});await db.SaveChangesAsync(ct);
            var creative=await db.PlatformAdCreatives.AsNoTracking().SingleAsync(x=>x.Id==item.CreativeId,ct);
            return Results.Created($"/api/v1/platform/advertising/campaigns/{item.Id}",ToResponse(item,creative,0,0,now));
        });
        platform.MapPut("/advertising/campaigns/{id:guid}",async(Guid id,PlatformAdCampaignRequest request,ClaimsPrincipal user,SmartPrintDbContext db,CancellationToken ct)=>{
            var actor=ActorId(user);if(actor is null)return Results.Forbid();
            var item=await db.PlatformAdCampaigns.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();
            var errors=await Validate(request,db,ct);if(errors.Count>0)return Results.ValidationProblem(errors);
            var before=new{item.Name,item.AdvertiserName,item.Headline,item.Body,item.Terms,item.CreativeId,item.DestinationUrl,item.Placement,item.Format,item.StartsUtc,item.EndsUtc,item.Status};
            item.Name=request.Name!.Trim();item.AdvertiserName=request.AdvertiserName!.Trim();item.Headline=request.Headline!.Trim();item.Body=request.Body!.Trim();item.Terms=Optional(request.Terms);item.CreativeId=request.CreativeId;item.DestinationUrl=request.DestinationUrl!.Trim();item.Placement=request.Placement!;item.Format=request.Format!;item.StartsUtc=request.StartsUtc;item.EndsUtc=request.EndsUtc;item.UpdatedByUserId=actor;item.UpdatedUtc=DateTimeOffset.UtcNow;
            Audit(db,item,actor.Value,"UPDATED",new{before,after=new{item.Name,item.AdvertiserName,item.Headline,item.Body,item.Terms,item.CreativeId,item.DestinationUrl,item.Placement,item.Format,item.StartsUtc,item.EndsUtc,item.Status}});
            await db.SaveChangesAsync(ct);
            var creative=await db.PlatformAdCreatives.AsNoTracking().SingleAsync(x=>x.Id==item.CreativeId,ct);
            var counts=await db.PlatformAdEvents.AsNoTracking().Where(x=>x.CampaignId==id).GroupBy(x=>x.EventType).Select(g=>new{g.Key,Count=g.LongCount()}).ToListAsync(ct);
            return Results.Ok(ToResponse(item,creative,counts.Where(x=>x.Key=="IMPRESSION").Select(x=>x.Count).FirstOrDefault(),counts.Where(x=>x.Key=="CLICK").Select(x=>x.Count).FirstOrDefault(),DateTimeOffset.UtcNow));
        });
        platform.MapPost("/advertising/campaigns/{id:guid}/status",async(Guid id,PlatformAdCampaignStatusRequest request,ClaimsPrincipal user,SmartPrintDbContext db,CancellationToken ct)=>{
            var actor=ActorId(user);if(actor is null)return Results.Forbid();
            if(request.Status is not("Active" or "Paused" or "Archived"))return Results.ValidationProblem(new Dictionary<string,string[]>{{"status",new[]{"Choose Active, Paused or Archived."}}});
            var item=await db.PlatformAdCampaigns.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();
            var before=item.Status;item.Status=request.Status;item.UpdatedByUserId=actor;item.UpdatedUtc=DateTimeOffset.UtcNow;Audit(db,item,actor.Value,"STATUS_CHANGED",new{before,after=item.Status});await db.SaveChangesAsync(ct);
            var creative=await db.PlatformAdCreatives.AsNoTracking().SingleAsync(x=>x.Id==item.CreativeId,ct);
            return Results.Ok(ToResponse(item,creative,0,0,DateTimeOffset.UtcNow));
        });
        platform.MapGet("/advertising/campaigns/{id:guid}/history",async(Guid id,SmartPrintDbContext db,CancellationToken ct)=>{
            if(!await db.PlatformAdCampaigns.AsNoTracking().AnyAsync(x=>x.Id==id,ct))return Results.NotFound();
            var rows=await db.PlatformAdCampaignAudits.AsNoTracking().Where(x=>x.CampaignId==id).OrderByDescending(x=>x.CreatedUtc).Take(100).Select(x=>new{x.Id,x.Action,x.DetailsJson,x.ActorUserId,x.CreatedUtc}).ToListAsync(ct);
            return Results.Ok(rows);
        });
        return platform;
    }
    public static WebApplication MapPublicAdvertisingEndpoints(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/advertising").AllowAnonymous().RequireRateLimiting("public-api");
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
        group.MapPost("/{id:guid}/events",async(Guid id,PlatformAdEventRequest request,SmartPrintDbContext db,CancellationToken ct)=>{
            if(request.EventType is not("IMPRESSION" or "CLICK"))return Results.ValidationProblem(new Dictionary<string,string[]>{{"eventType",new[]{"Use IMPRESSION or CLICK."}}});
            var now=DateTimeOffset.UtcNow;var item=await db.PlatformAdCampaigns.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);
            if(item is null||!PlatformAdvertisingRules.IsActiveNow(item,now))return Results.NotFound();
            db.PlatformAdEvents.Add(new PlatformAdEvent{CampaignId=id,EventType=request.EventType,Placement=item.Placement,CreatedUtc=now});await db.SaveChangesAsync(ct);
            return Results.Accepted();
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
    private static async Task<Dictionary<string,string[]>> Validate(PlatformAdCampaignRequest request,SmartPrintDbContext db,CancellationToken ct)
    {
        var e=new Dictionary<string,string[]>();
        Required(e,"name",request.Name,120);Required(e,"advertiserName",request.AdvertiserName,140);Required(e,"headline",request.Headline,120);Required(e,"body",request.Body,400);
        if(request.Terms is {Length:>2000})e["terms"]=new[]{"Terms must be 2,000 characters or fewer."};
        if(request.CreativeId==Guid.Empty||!await db.PlatformAdCreatives.AnyAsync(x=>x.Id==request.CreativeId,ct))e["creativeId"]=new[]{"Upload a banner image before saving."};
        if(!PlatformAdvertisingRules.IsValidPlacementFormat(request.Placement,request.Format))e["placement"]=new[]{"Choose Home banner, Files card or Shop overview banner."};
        if(!PlatformAdvertisingRules.IsSafeDestinationUrl(request.DestinationUrl))e["destinationUrl"]=new[]{"Use a public HTTPS destination URL on port 443."};
        if(request.EndsUtc is not null&&request.EndsUtc<request.StartsUtc)e["endsUtc"]=new[]{"The end time cannot be before the start time."};
        return e;
    }
    private static void Required(Dictionary<string,string[]> e,string key,string? value,int max){if(string.IsNullOrWhiteSpace(value)||value.Trim().Length>max)e[key]=new[]{$"Required; maximum {max} characters."};}
    private static string? Optional(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static Guid? ActorId(ClaimsPrincipal user){var text=user.FindFirstValue(JwtRegisteredClaimNames.Sub)??user.FindFirstValue(ClaimTypes.NameIdentifier);return Guid.TryParse(text,out var id)?id:null;}
    private static void Audit(SmartPrintDbContext db,PlatformAdCampaign item,Guid actor,string action,object details)=>db.PlatformAdCampaignAudits.Add(new PlatformAdCampaignAudit{CampaignId=item.Id,ActorUserId=actor,Action=action,DetailsJson=JsonSerializer.Serialize(details),CreatedUtc=DateTimeOffset.UtcNow});
    private static object ToResponse(PlatformAdCampaign x,PlatformAdCreative? c,long impressions,long clicks,DateTimeOffset now)=>new{
        x.Id,x.Name,x.AdvertiserName,x.Headline,x.Body,x.Terms,x.CreativeId,creativeUrl=c is null?null:$"/api/v1/advertising/creatives/{c.Id}",creativeFileName=c?.FileName,x.DestinationUrl,x.Placement,x.Format,
        status=PlatformAdvertisingRules.DisplayStatus(x,now),storedStatus=x.Status,x.StartsUtc,x.EndsUtc,x.CreatedUtc,x.UpdatedUtc,impressions,clicks,clickThroughRate=impressions==0?0:Math.Round((double)clicks/impressions*100,2)};
}
public sealed record PlatformAdCampaignRequest(string? Name,string? AdvertiserName,string? Headline,string? Body,string? Terms,Guid CreativeId,string? DestinationUrl,string? Placement,string? Format,DateTimeOffset StartsUtc,DateTimeOffset? EndsUtc);
public sealed record PlatformAdCampaignStatusRequest(string? Status);
public sealed record PlatformAdEventRequest(string? EventType);
