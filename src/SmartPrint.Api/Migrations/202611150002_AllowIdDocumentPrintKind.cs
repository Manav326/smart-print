using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611150002_AllowIdDocumentPrintKind")]
public sealed class AllowIdDocumentPrintKind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PrintKind";

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_PrintKind"
                CHECK ("PrintKind" IN ('DOCUMENT', 'PHOTO', 'ID_DOCUMENT'));

            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PhotoQuoteSnapshot";

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_PhotoQuoteSnapshot"
                CHECK (
                    (
                        "PrintKind" = 'DOCUMENT' AND
                        "PhotoSizeCode" IS NULL AND
                        "PhotoCount" = 0 AND
                        "PhotoUnitPriceMinorSnapshot" = 0
                    )
                    OR
                    (
                        "PrintKind" = 'PHOTO' AND
                        "PhotoSizeCode" IN ('35x45', '25x35', '2x2', '4x6') AND
                        "PhotoCount" BETWEEN 1 AND 100 AND
                        "PhotoUnitPriceMinorSnapshot" > 0
                    )
                    OR
                    (
                        "PrintKind" = 'PHOTO' AND
                        "PhotoSizeCode" IN ('35x45', '25x35', '2x2', '4x6') AND
                        "PhotoCount" BETWEEN 1 AND 100 AND
                        "PhotoUnitPriceMinorSnapshot" = 0 AND
                        "Status" = 'AWAITING_CONFIGURATION' AND
                        "QuotedAmountMinor" = 0 AND
                        "PricingVersion" IS NULL AND
                        "QuoteExpiresUtc" IS NULL
                    )
                    OR
                    (
                        "PrintKind" = 'ID_DOCUMENT' AND
                        "PageCount" = 1 AND
                        "Copies" = 1 AND
                        "PhotoSizeCode" IS NULL AND
                        "PhotoCount" = 0 AND
                        "PhotoUnitPriceMinorSnapshot" = 0
                    )
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $migration$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "PrintJobs"
                    WHERE "PrintKind" = 'ID_DOCUMENT'
                ) THEN
                    RAISE EXCEPTION
                        'Cannot remove ID document print kind while ID document print jobs exist.';
                END IF;
            END;
            $migration$;

            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PhotoQuoteSnapshot";

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_PhotoQuoteSnapshot"
                CHECK (
                    (
                        "PrintKind" = 'DOCUMENT' AND
                        "PhotoSizeCode" IS NULL AND
                        "PhotoCount" = 0 AND
                        "PhotoUnitPriceMinorSnapshot" = 0
                    )
                    OR
                    (
                        "PrintKind" = 'PHOTO' AND
                        "PhotoSizeCode" IN ('35x45', '25x35', '2x2', '4x6') AND
                        "PhotoCount" BETWEEN 1 AND 100 AND
                        "PhotoUnitPriceMinorSnapshot" > 0
                    )
                    OR
                    (
                        "PrintKind" = 'PHOTO' AND
                        "PhotoSizeCode" IN ('35x45', '25x35', '2x2', '4x6') AND
                        "PhotoCount" BETWEEN 1 AND 100 AND
                        "PhotoUnitPriceMinorSnapshot" = 0 AND
                        "Status" = 'AWAITING_CONFIGURATION' AND
                        "QuotedAmountMinor" = 0 AND
                        "PricingVersion" IS NULL AND
                        "QuoteExpiresUtc" IS NULL
                    )
                );

            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PrintKind";

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_PrintKind"
                CHECK ("PrintKind" IN ('DOCUMENT', 'PHOTO'));
            """);
    }
}
