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
using SmartPrint.Api.Modules.PlatformAdvertising.Services;

namespace SmartPrint.Api.Endpoints;
public static class PlatformAdvertisingEndpoints
{
    public static RouteGroupBuilder MapPlatformAdvertisingEndpoints(this RouteGroupBuilder platform)
    {
        
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
        

        platform.MapPost("/advertising/campaigns",async(PlatformAdCampaignRequest request,ClaimsPrincipal user,SmartPrintDbContext db,CancellationToken ct)=>{
            var actor=ActorId(user);if(actor is null)return Results.Forbid();
            var errors=await Validate(request,db,ct);if(errors.Count>0)return Results.ValidationProblem(errors);
            var now=DateTimeOffset.UtcNow;
            var item=new PlatformAdCampaign{Name=request.Name!.Trim(),AdvertiserName=request.AdvertiserName!.Trim(),Headline=request.Headline!.Trim(),Body=request.Body!.Trim(),Terms=Optional(request.Terms),CreativeId=request.CreativeId,DestinationUrl=request.DestinationUrl!.Trim(),Placement=request.Placement!,Format=request.Format!,StartsUtc=request.StartsUtc,EndsUtc=request.EndsUtc,Status="Draft",CreatedByUserId=actor,UpdatedByUserId=actor,CreatedUtc=now,UpdatedUtc=now};
            db.PlatformAdCampaigns.Add(item);Audit(db,item,actor.Value,"CREATED",new{after=item});await db.SaveChangesAsync(ct);
            var creative=await db.PlatformAdCreatives.AsNoTracking().SingleAsync(x=>x.Id==item.CreativeId,ct);
            return Results.Created($"/api/v1/platform/advertising/campaigns/{item.Id}",PlatformAdvertisingResponseMapper.ToResponse(item,creative,0,0,now));
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
            return Results.Ok(PlatformAdvertisingResponseMapper.ToResponse(item,creative,counts.Where(x=>x.Key=="IMPRESSION").Select(x=>x.Count).FirstOrDefault(),counts.Where(x=>x.Key=="CLICK").Select(x=>x.Count).FirstOrDefault(),DateTimeOffset.UtcNow));
        });
        platform.MapPost("/advertising/campaigns/{id:guid}/status",async(Guid id,PlatformAdCampaignStatusRequest request,ClaimsPrincipal user,SmartPrintDbContext db,CancellationToken ct)=>{
            var actor=ActorId(user);if(actor is null)return Results.Forbid();
            if(request.Status is not("Active" or "Paused" or "Archived"))return Results.ValidationProblem(new Dictionary<string,string[]>{{"status",new[]{"Choose Active, Paused or Archived."}}});
            var item=await db.PlatformAdCampaigns.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();
            var before=item.Status;item.Status=request.Status;item.UpdatedByUserId=actor;item.UpdatedUtc=DateTimeOffset.UtcNow;Audit(db,item,actor.Value,"STATUS_CHANGED",new{before,after=item.Status});await db.SaveChangesAsync(ct);
            var creative=await db.PlatformAdCreatives.AsNoTracking().SingleAsync(x=>x.Id==item.CreativeId,ct);
            return Results.Ok(PlatformAdvertisingResponseMapper.ToResponse(item,creative,0,0,DateTimeOffset.UtcNow));
        });
        
        return platform;
    }
    public static WebApplication MapPublicAdvertisingEndpoints(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/advertising").AllowAnonymous().RequireRateLimiting("public-api");
        
        group.MapPost("/{id:guid}/events",async(Guid id,PlatformAdEventRequest request,SmartPrintDbContext db,CancellationToken ct)=>{
            if(request.EventType is not("IMPRESSION" or "CLICK"))return Results.ValidationProblem(new Dictionary<string,string[]>{{"eventType",new[]{"Use IMPRESSION or CLICK."}}});
            var now=DateTimeOffset.UtcNow;var item=await db.PlatformAdCampaigns.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);
            if(item is null||!PlatformAdvertisingRules.IsActiveNow(item,now))return Results.NotFound();
            db.PlatformAdEvents.Add(new PlatformAdEvent{CampaignId=id,EventType=request.EventType,Placement=item.Placement,CreatedUtc=now});await db.SaveChangesAsync(ct);
            return Results.Accepted();
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
}
public sealed record PlatformAdCampaignRequest(string? Name,string? AdvertiserName,string? Headline,string? Body,string? Terms,Guid CreativeId,string? DestinationUrl,string? Placement,string? Format,DateTimeOffset StartsUtc,DateTimeOffset? EndsUtc);
public sealed record PlatformAdCampaignStatusRequest(string? Status);
public sealed record PlatformAdEventRequest(string? EventType);
