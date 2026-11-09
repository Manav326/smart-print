using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611070002_AddShopOriginatedPrintJobs")]
public sealed class AddShopOriginatedPrintJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ALTER COLUMN "QrStationId" DROP NOT NULL,
                ALTER COLUMN "CustomerSessionId" DROP NOT NULL,
                ADD COLUMN "CustomerUserId" uuid NULL,
                ADD COLUMN "Origin" varchar(24) NOT NULL DEFAULT 'CUSTOMER_QR',
                ADD COLUMN "RecipientName" varchar(160) NULL,
                ADD COLUMN "RecipientPhone" varchar(40) NULL;

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_Origin"
                CHECK ("Origin" IN ('CUSTOMER_QR', 'SHOP_OWNER'));

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "FK_PrintJobs_AspNetUsers_CustomerUserId"
                FOREIGN KEY ("CustomerUserId") REFERENCES "AspNetUsers" ("Id")
                ON DELETE SET NULL;

            CREATE INDEX "IX_PrintJobs_TenantId_CustomerUserId"
                ON "PrintJobs" ("TenantId", "CustomerUserId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM "PrintJobs" WHERE "Origin" = 'SHOP_OWNER') THEN
                    RAISE EXCEPTION 'Cannot roll back shop-originated print jobs while any such jobs exist.';
                END IF;
            END $$;

            DROP INDEX IF EXISTS "IX_PrintJobs_TenantId_CustomerUserId";
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "FK_PrintJobs_AspNetUsers_CustomerUserId",
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_Origin",
                DROP COLUMN IF EXISTS "RecipientPhone",
                DROP COLUMN IF EXISTS "RecipientName",
                DROP COLUMN IF EXISTS "Origin",
                DROP COLUMN IF EXISTS "CustomerUserId",
                ALTER COLUMN "QrStationId" SET NOT NULL,
                ALTER COLUMN "CustomerSessionId" SET NOT NULL;
            """);
    }
}
