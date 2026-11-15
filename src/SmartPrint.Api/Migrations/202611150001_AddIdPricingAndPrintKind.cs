using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611150001_AddIdPricingAndPrintKind")]
public sealed class AddIdPricingAndPrintKind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "ShopPricingProfiles"
                ADD COLUMN "IdDocumentPriceMinor" bigint NOT NULL DEFAULT 0;

            ALTER TABLE "ShopPricingProfiles"
                ADD CONSTRAINT "CK_ShopPricingProfiles_IdDocumentPrice"
                CHECK ("IdDocumentPriceMinor" >= 0);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "ShopPricingProfiles"
                DROP CONSTRAINT IF EXISTS "CK_ShopPricingProfiles_IdDocumentPrice";

            ALTER TABLE "ShopPricingProfiles"
                DROP COLUMN IF EXISTS "IdDocumentPriceMinor";
            """);
    }
}
