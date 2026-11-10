using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611100001_AddRazorpayShopSubscriptions")]
public sealed class AddRazorpayShopSubscriptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "ShopSubscriptionPayments" (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "CreatedByUserId" uuid NOT NULL,
                "PlanVersionId" uuid NOT NULL,
                "PlanCode" varchar(40) NOT NULL,
                "BillingInterval" varchar(16) NOT NULL,
                "AmountMinor" bigint NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "ClientRequestId" varchar(100) NOT NULL,
                "RazorpayOrderId" varchar(80) NOT NULL,
                "RazorpayPaymentId" varchar(80),
                "Status" varchar(32) NOT NULL,
                "AssignedSubscriptionId" uuid,
                "CreatedUtc" timestamptz NOT NULL,
                "UpdatedUtc" timestamptz NOT NULL,
                "VerifiedUtc" timestamptz,
                CONSTRAINT "PK_ShopSubscriptionPayments" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ShopSubscriptionPayments_Tenants_TenantId"
                    FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ShopSubscriptionPayments_AspNetUsers_CreatedByUserId"
                    FOREIGN KEY ("CreatedByUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_ShopSubscriptionPayments_SubscriptionPlanVersions_PlanVersionId"
                    FOREIGN KEY ("PlanVersionId") REFERENCES "SubscriptionPlanVersions" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_ShopSubscriptionPayments_TenantSubscriptions_AssignedSubscriptionId"
                    FOREIGN KEY ("AssignedSubscriptionId") REFERENCES "TenantSubscriptions" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "CK_ShopSubscriptionPayments_AmountMinor_Positive" CHECK ("AmountMinor" > 0)
            );

            CREATE UNIQUE INDEX "IX_ShopSubscriptionPayments_TenantId_ClientRequestId"
                ON "ShopSubscriptionPayments" ("TenantId", "ClientRequestId");
            CREATE UNIQUE INDEX "IX_ShopSubscriptionPayments_RazorpayOrderId"
                ON "ShopSubscriptionPayments" ("RazorpayOrderId");
            CREATE UNIQUE INDEX "IX_ShopSubscriptionPayments_RazorpayPaymentId"
                ON "ShopSubscriptionPayments" ("RazorpayPaymentId")
                WHERE "RazorpayPaymentId" IS NOT NULL;
            CREATE INDEX "IX_ShopSubscriptionPayments_TenantId_CreatedUtc"
                ON "ShopSubscriptionPayments" ("TenantId", "CreatedUtc");

            CREATE TABLE "RazorpayWebhookEvents" (
                "Id" uuid NOT NULL,
                "EventId" varchar(100) NOT NULL,
                "EventType" varchar(64) NOT NULL,
                "RazorpayOrderId" varchar(80),
                "RazorpayPaymentId" varchar(80),
                "PayloadSha256" varchar(64) NOT NULL,
                "Status" varchar(32) NOT NULL,
                "Reason" varchar(500),
                "ReceivedUtc" timestamptz NOT NULL,
                "ProcessedUtc" timestamptz,
                CONSTRAINT "PK_RazorpayWebhookEvents" PRIMARY KEY ("Id")
            );
            CREATE UNIQUE INDEX "IX_RazorpayWebhookEvents_EventId"
                ON "RazorpayWebhookEvents" ("EventId");
            CREATE INDEX "IX_RazorpayWebhookEvents_RazorpayOrderId_ReceivedUtc"
                ON "RazorpayWebhookEvents" ("RazorpayOrderId", "ReceivedUtc");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "RazorpayWebhookEvents";
            DROP TABLE IF EXISTS "ShopSubscriptionPayments";
            """);
    }
}
