using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;
namespace SmartPrint.Api.Migrations;
[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611140001_AddPlatformAdvertising")]
public sealed class AddPlatformAdvertising : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.CreateTable("PlatformAdCreatives",table=>new{
            Id=table.Column<Guid>(type:"uuid",nullable:false),
            FileName=table.Column<string>(type:"character varying(255)",maxLength:255,nullable:false),
            ContentType=table.Column<string>(type:"character varying(40)",maxLength:40,nullable:false),
            StorageKey=table.Column<string>(type:"character varying(300)",maxLength:300,nullable:false),
            SizeBytes=table.Column<long>(type:"bigint",nullable:false),
            CreatedByUserId=table.Column<Guid>(type:"uuid",nullable:true),
            CreatedUtc=table.Column<DateTimeOffset>(type:"timestamp with time zone",nullable:false)
        },constraints:table=>{
            table.PrimaryKey("PK_PlatformAdCreatives",x=>x.Id);
            table.ForeignKey("FK_PlatformAdCreatives_AspNetUsers_CreatedByUserId",x=>x.CreatedByUserId,"AspNetUsers","Id",onDelete:ReferentialAction.SetNull);
            table.CheckConstraint("CK_PlatformAdCreatives_Size","\"SizeBytes\" BETWEEN 1 AND 3145728");
            table.CheckConstraint("CK_PlatformAdCreatives_ContentType","\"ContentType\" IN ('image/jpeg','image/png','image/webp')");
        });
        m.CreateTable("PlatformAdCampaigns",table=>new{
            Id=table.Column<Guid>(type:"uuid",nullable:false),Name=table.Column<string>(type:"character varying(120)",maxLength:120,nullable:false),
            AdvertiserName=table.Column<string>(type:"character varying(140)",maxLength:140,nullable:false),Headline=table.Column<string>(type:"character varying(120)",maxLength:120,nullable:false),
            Body=table.Column<string>(type:"character varying(400)",maxLength:400,nullable:false),Terms=table.Column<string>(type:"character varying(2000)",maxLength:2000,nullable:true),
            CreativeId=table.Column<Guid>(type:"uuid",nullable:false),DestinationUrl=table.Column<string>(type:"character varying(1000)",maxLength:1000,nullable:false),
            Placement=table.Column<string>(type:"character varying(20)",maxLength:20,nullable:false),Format=table.Column<string>(type:"character varying(20)",maxLength:20,nullable:false),
            Status=table.Column<string>(type:"character varying(20)",maxLength:20,nullable:false),StartsUtc=table.Column<DateTimeOffset>(type:"timestamp with time zone",nullable:false),
            EndsUtc=table.Column<DateTimeOffset>(type:"timestamp with time zone",nullable:true),CreatedByUserId=table.Column<Guid>(type:"uuid",nullable:true),
            UpdatedByUserId=table.Column<Guid>(type:"uuid",nullable:true),CreatedUtc=table.Column<DateTimeOffset>(type:"timestamp with time zone",nullable:false),
            UpdatedUtc=table.Column<DateTimeOffset>(type:"timestamp with time zone",nullable:false)
        },constraints:table=>{
            table.PrimaryKey("PK_PlatformAdCampaigns",x=>x.Id);
            table.ForeignKey("FK_PlatformAdCampaigns_PlatformAdCreatives_CreativeId",x=>x.CreativeId,"PlatformAdCreatives","Id",onDelete:ReferentialAction.Restrict);
            table.ForeignKey("FK_PlatformAdCampaigns_AspNetUsers_CreatedByUserId",x=>x.CreatedByUserId,"AspNetUsers","Id",onDelete:ReferentialAction.SetNull);
            table.ForeignKey("FK_PlatformAdCampaigns_AspNetUsers_UpdatedByUserId",x=>x.UpdatedByUserId,"AspNetUsers","Id",onDelete:ReferentialAction.SetNull);
            table.CheckConstraint("CK_PlatformAdCampaigns_Status","\"Status\" IN ('Draft','Active','Paused','Archived')");
            table.CheckConstraint("CK_PlatformAdCampaigns_Placement","\"Placement\" IN ('HOME','FILES','SHOP_OVERVIEW')");
            table.CheckConstraint("CK_PlatformAdCampaigns_Format","\"Format\" IN ('BANNER','CARD')");
            table.CheckConstraint("CK_PlatformAdCampaigns_Dates","\"EndsUtc\" IS NULL OR \"EndsUtc\" >= \"StartsUtc\"");
        });
        m.CreateTable("PlatformAdEvents",table=>new{
            Id=table.Column<Guid>(type:"uuid",nullable:false),CampaignId=table.Column<Guid>(type:"uuid",nullable:false),
            EventType=table.Column<string>(type:"character varying(16)",maxLength:16,nullable:false),Placement=table.Column<string>(type:"character varying(20)",maxLength:20,nullable:false),
            CreatedUtc=table.Column<DateTimeOffset>(type:"timestamp with time zone",nullable:false)
        },constraints:table=>{
            table.PrimaryKey("PK_PlatformAdEvents",x=>x.Id);
            table.ForeignKey("FK_PlatformAdEvents_PlatformAdCampaigns_CampaignId",x=>x.CampaignId,"PlatformAdCampaigns","Id",onDelete:ReferentialAction.Cascade);
            table.CheckConstraint("CK_PlatformAdEvents_EventType","\"EventType\" IN ('IMPRESSION','CLICK')");
            table.CheckConstraint("CK_PlatformAdEvents_Placement","\"Placement\" IN ('HOME','FILES','SHOP_OVERVIEW')");
        });
        m.CreateTable("PlatformAdCampaignAudits",table=>new{
            Id=table.Column<Guid>(type:"uuid",nullable:false),CampaignId=table.Column<Guid>(type:"uuid",nullable:false),
            ActorUserId=table.Column<Guid>(type:"uuid",nullable:true),Action=table.Column<string>(type:"character varying(32)",maxLength:32,nullable:false),
            DetailsJson=table.Column<string>(type:"jsonb",nullable:false),CreatedUtc=table.Column<DateTimeOffset>(type:"timestamp with time zone",nullable:false)
        },constraints:table=>{
            table.PrimaryKey("PK_PlatformAdCampaignAudits",x=>x.Id);
            table.ForeignKey("FK_PlatformAdCampaignAudits_PlatformAdCampaigns_CampaignId",x=>x.CampaignId,"PlatformAdCampaigns","Id",onDelete:ReferentialAction.Cascade);
            table.ForeignKey("FK_PlatformAdCampaignAudits_AspNetUsers_ActorUserId",x=>x.ActorUserId,"AspNetUsers","Id",onDelete:ReferentialAction.SetNull);
        });
        m.CreateIndex("IX_PlatformAdCreatives_CreatedByUserId","PlatformAdCreatives","CreatedByUserId");
        m.CreateIndex("IX_PlatformAdCreatives_CreatedUtc","PlatformAdCreatives","CreatedUtc");
        m.CreateIndex("IX_PlatformAdCreatives_StorageKey","PlatformAdCreatives","StorageKey",unique:true);
        m.CreateIndex("IX_PlatformAdCampaigns_CreatedByUserId","PlatformAdCampaigns","CreatedByUserId");
        m.CreateIndex("IX_PlatformAdCampaigns_UpdatedByUserId","PlatformAdCampaigns","UpdatedByUserId");
        m.CreateIndex("IX_PlatformAdCampaigns_CreativeId","PlatformAdCampaigns","CreativeId");
        m.CreateIndex("IX_PlatformAdCampaigns_Placement_Status_StartsUtc_EndsUtc","PlatformAdCampaigns",new[]{"Placement","Status","StartsUtc","EndsUtc"});
        m.CreateIndex("IX_PlatformAdEvents_CampaignId_EventType_CreatedUtc","PlatformAdEvents",new[]{"CampaignId","EventType","CreatedUtc"});
        m.CreateIndex("IX_PlatformAdEvents_CreatedUtc","PlatformAdEvents","CreatedUtc");
        m.CreateIndex("IX_PlatformAdCampaignAudits_CampaignId_CreatedUtc","PlatformAdCampaignAudits",new[]{"CampaignId","CreatedUtc"});
        m.CreateIndex("IX_PlatformAdCampaignAudits_ActorUserId","PlatformAdCampaignAudits","ActorUserId");
    }
    protected override void Down(MigrationBuilder m)
    { m.DropTable("PlatformAdCampaignAudits");m.DropTable("PlatformAdEvents");m.DropTable("PlatformAdCampaigns");m.DropTable("PlatformAdCreatives"); }
}
