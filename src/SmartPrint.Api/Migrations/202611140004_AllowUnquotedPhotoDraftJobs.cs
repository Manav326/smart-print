using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611140004_AllowUnquotedPhotoDraftJobs")]
public sealed class AllowUnquotedPhotoDraftJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
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
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $migration$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM "PrintJobs"
                    WHERE "PrintKind" = 'PHOTO'
                      AND "PhotoSizeCode" IN ('35x45', '25x35', '2x2', '4x6')
                      AND "PhotoCount" BETWEEN 1 AND 100
                      AND "PhotoUnitPriceMinorSnapshot" = 0
                      AND "Status" = 'AWAITING_CONFIGURATION'
                      AND "QuotedAmountMinor" = 0
                      AND "PricingVersion" IS NULL
                      AND "QuoteExpiresUtc" IS NULL
                ) THEN
                    RAISE EXCEPTION
                        'Cannot roll back unquoted photo draft support while unquoted photo draft jobs exist.';
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
                );
            """);
    }
}
