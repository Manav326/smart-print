using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611160001_AddPhotoMediaAndOutputLayoutSnapshots")]
public sealed class AddPhotoMediaAndOutputLayoutSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "PhotoPaperFinish" varchar(12) NOT NULL DEFAULT 'PLAIN',
                ADD COLUMN "PhotoOutputLayout" varchar(24) NOT NULL DEFAULT 'A4_SHEET';

            ALTER TABLE "LocalPrintUploadTickets"
                ADD COLUMN "PhotoPaperFinish" varchar(12) NOT NULL DEFAULT 'GLOSSY',
                ADD COLUMN "PhotoOutputLayout" varchar(24) NOT NULL DEFAULT 'A4_SHEET';

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_PhotoOutputOptions"
                CHECK (
                    "PhotoPaperFinish" IN ('PLAIN', 'GLOSSY', 'MATTE') AND
                    "PhotoOutputLayout" IN ('A4_SHEET', 'STANDALONE_4X6')
                );
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PhotoOutputOptions",
                DROP COLUMN IF EXISTS "PhotoOutputLayout",
                DROP COLUMN IF EXISTS "PhotoPaperFinish";

            ALTER TABLE "LocalPrintUploadTickets"
                DROP COLUMN IF EXISTS "PhotoOutputLayout",
                DROP COLUMN IF EXISTS "PhotoPaperFinish";
        """);
    }
}