using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611100001_AddRazorpayShopSubscriptionPayments")]
public sealed class AddRazorpayShopSubscriptionPayments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "ShopSubscriptionPayments" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "PlanVersionId" uuid NOT NULL,
                "PlanCode" varchar(40) NOT NULL,
                "PlanName" varchar(100) NOT NULL,
                "BillingInterval" varchar(16) NOT NULL,
                "AmountMinor" bigint NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "IdempotencyKey" varchar(100) NOT NULL,
                "RazorpayOrderId" varchar(100) NOT NULL,
                "RazorpayPaymentId" varchar(100),
                "Status" varchar(16) NOT NULL,
                "AppliedSubscriptionId" uuid,
                "FailureCode" varchar(80),
                "CreatedUtc" timestamptz NOT NULL,
                "UpdatedUtc" timestamptz NOT NULL,
                "CapturedUtc" timestamptz,
                CONSTRAINT "FK_ShopSubscriptionPayments_Tenants_TenantId"
                    FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_ShopSubscriptionPayments_SubscriptionPlanVersions_PlanVersionId"
                    FOREIGN KEY ("PlanVersionId") REFERENCES "SubscriptionPlanVersions" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_ShopSubscriptionPayments_TenantSubscriptions_AppliedSubscriptionId"
                    FOREIGN KEY ("AppliedSubscriptionId") REFERENCES "TenantSubscriptions" ("Id") ON DELETE SET NULL,
                CONSTRAINT "CK_ShopSubscriptionPayments_AmountMinor" CHECK ("AmountMinor" > 0),
                CONSTRAINT "CK_ShopSubscriptionPayments_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
                CONSTRAINT "CK_ShopSubscriptionPayments_Status" CHECK ("Status" IN ('Pending', 'Authorized', 'Captured', 'Applied', 'Failed')),
                CONSTRAINT "CK_ShopSubscriptionPayments_BillingInterval" CHECK ("BillingInterval" IN ('Monthly', 'Annual'))
            );
            CREATE UNIQUE INDEX "IX_ShopSubscriptionPayments_TenantId_IdempotencyKey"
                ON "ShopSubscriptionPayments" ("TenantId", "IdempotencyKey");
            CREATE UNIQUE INDEX "IX_ShopSubscriptionPayments_RazorpayOrderId"
                ON "ShopSubscriptionPayments" ("RazorpayOrderId");
            CREATE UNIQUE INDEX "IX_ShopSubscriptionPayments_RazorpayPaymentId"
                ON "ShopSubscriptionPayments" ("RazorpayPaymentId")
                WHERE "RazorpayPaymentId" IS NOT NULL;
            CREATE INDEX "IX_ShopSubscriptionPayments_TenantId_CreatedUtc"
                ON "ShopSubscriptionPayments" ("TenantId", "CreatedUtc" DESC);
            CREATE INDEX "IX_ShopSubscriptionPayments_AppliedSubscriptionId"
                ON "ShopSubscriptionPayments" ("AppliedSubscriptionId");

            CREATE TABLE "RazorpayWebhookEvents" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "EventId" varchar(100) NOT NULL,
                "EventType" varchar(80) NOT NULL,
                "RazorpayOrderId" varchar(100),
                "RazorpayPaymentId" varchar(100),
                "Status" varchar(16) NOT NULL,
                "LastError" varchar(100),
                "ReceivedUtc" timestamptz NOT NULL,
                "ProcessedUtc" timestamptz,
                CONSTRAINT "CK_RazorpayWebhookEvents_Status" CHECK ("Status" IN ('Pending', 'Processed'))
            );
            CREATE UNIQUE INDEX "IX_RazorpayWebhookEvents_EventId"
                ON "RazorpayWebhookEvents" ("EventId");
            CREATE INDEX "IX_RazorpayWebhookEvents_Status_ReceivedUtc"
                ON "RazorpayWebhookEvents" ("Status", "ReceivedUtc" DESC);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM "ShopSubscriptionPayments") THEN
                    RAISE EXCEPTION 'Cannot remove shop subscription payment records during migration rollback.';
                END IF;
            END $$;

            DROP TABLE IF EXISTS "RazorpayWebhookEvents";
            DROP INDEX IF EXISTS "IX_ShopSubscriptionPayments_AppliedSubscriptionId";
            DROP TABLE IF EXISTS "ShopSubscriptionPayments";
            """);
    }
}
