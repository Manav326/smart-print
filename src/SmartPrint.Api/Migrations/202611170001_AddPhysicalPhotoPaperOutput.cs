using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611170001_AddPhysicalPhotoPaperOutput")]
public sealed class AddPhysicalPhotoPaperOutput : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "PhotoPaperFinish" varchar(12) NOT NULL DEFAULT 'PLAIN',
                ADD COLUMN "PhotoOutputLayout" varchar(24) NOT NULL DEFAULT 'A4_SHEET';

            ALTER TABLE "LocalPrintUploadTickets"
                ADD COLUMN "PhotoPaperFinish" varchar(12) NOT NULL DEFAULT 'PLAIN',
                ADD COLUMN "PhotoOutputLayout" varchar(24) NOT NULL DEFAULT 'A4_SHEET';

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_PhotoOutputOptions"
                CHECK (
                    "PhotoPaperFinish" IN ('PLAIN', 'GLOSSY', 'MATTE') AND
                    "PhotoOutputLayout" IN ('A4_SHEET', 'PHOTO_PAPER')
                );

            ALTER TABLE "LocalPrintUploadTickets"
                ADD CONSTRAINT "CK_LocalPrintUploadTickets_PhotoOutputOptions"
                CHECK (
                    "PhotoPaperFinish" IN ('PLAIN', 'GLOSSY', 'MATTE') AND
                    "PhotoOutputLayout" IN ('A4_SHEET', 'PHOTO_PAPER')
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "LocalPrintUploadTickets"
                DROP CONSTRAINT IF EXISTS "CK_LocalPrintUploadTickets_PhotoOutputOptions",
                DROP COLUMN IF EXISTS "PhotoOutputLayout",
                DROP COLUMN IF EXISTS "PhotoPaperFinish";

            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PhotoOutputOptions",
                DROP COLUMN IF EXISTS "PhotoOutputLayout",
                DROP COLUMN IF EXISTS "PhotoPaperFinish";
            """);
    }
}
