using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611070001_AddPhotoSheetDensitySnapshot")]
public sealed class AddPhotoSheetDensitySnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                ADD COLUMN "PhotosPerPage" integer NULL;

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_PhotoSheetDensity"
                CHECK (
                    (
                        "PrintKind" = 'DOCUMENT' AND
                        "PhotosPerPage" IS NULL
                    )
                    OR
                    (
                        "PrintKind" = 'PHOTO' AND
                        (
                            "PhotosPerPage" IS NULL
                            OR "PhotosPerPage" BETWEEN 1 AND
                                CASE "PhotoSizeCode"
                                    WHEN '35x45' THEN 25
                                    WHEN '25x35' THEN 49
                                    WHEN '2x2' THEN 15
                                    WHEN '4x6' THEN 1
                                    ELSE 0
                                END
                        )
                    )
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_PhotoSheetDensity",
                DROP COLUMN IF EXISTS "PhotosPerPage";
            """);
    }
}
