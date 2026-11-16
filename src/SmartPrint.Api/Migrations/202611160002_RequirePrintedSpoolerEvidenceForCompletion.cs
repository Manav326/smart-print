using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611160002_RequirePrintedSpoolerEvidenceForCompletion")]
public sealed class RequirePrintedSpoolerEvidenceForCompletion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "SpoolerPagesPrinted" integer NOT NULL DEFAULT 0,
                ADD COLUMN "SpoolerTotalPages" integer NOT NULL DEFAULT 0,
                ADD CONSTRAINT "CK_PrintJobs_SpoolerPageCounts"
                CHECK (
                    "SpoolerPagesPrinted" >= 0 AND
                    "SpoolerTotalPages" >= 0 AND
                    "SpoolerPagesPrinted" <= "SpoolerTotalPages"
                );
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_SpoolerPageCounts",
                DROP COLUMN IF EXISTS "SpoolerPagesPrinted",
                DROP COLUMN IF EXISTS "SpoolerTotalPages";
        """);
    }
}