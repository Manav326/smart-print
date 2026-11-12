using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611120002_AddLocalUploadTicketPhotoMetadata")]
public sealed class AddLocalUploadTicketPhotoMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "LocalPrintUploadTickets"
                ADD COLUMN "PhotoSizeCode" character varying(8) NULL,
                ADD COLUMN "PhotoCount" integer NULL,
                ADD COLUMN "PhotosPerPage" integer NULL;

            ALTER TABLE "LocalPrintUploadTickets"
                ADD CONSTRAINT "CK_LocalPrintUploadTickets_PhotoMetadata"
                CHECK (
                    (
                        "PhotoSizeCode" IS NULL AND
                        "PhotoCount" IS NULL AND
                        "PhotosPerPage" IS NULL
                    )
                    OR
                    (
                        "PrintKind" = 'PHOTO' AND
                        "PhotoSizeCode" IN ('35x45', '25x35', '2x2', '4x6') AND
                        "PhotoCount" BETWEEN 1 AND 100 AND
                        "PhotosPerPage" BETWEEN 1 AND
                            CASE "PhotoSizeCode"
                                WHEN '35x45' THEN 25
                                WHEN '25x35' THEN 49
                                WHEN '2x2' THEN 15
                                WHEN '4x6' THEN 1
                                ELSE 0
                            END
                    )
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "LocalPrintUploadTickets"
                DROP CONSTRAINT IF EXISTS "CK_LocalPrintUploadTickets_PhotoMetadata";
            ALTER TABLE "LocalPrintUploadTickets"
                DROP COLUMN IF EXISTS "PhotoSizeCode",
                DROP COLUMN IF EXISTS "PhotoCount",
                DROP COLUMN IF EXISTS "PhotosPerPage";
            """);
    }
}
