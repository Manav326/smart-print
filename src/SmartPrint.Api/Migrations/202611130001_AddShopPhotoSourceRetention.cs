using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611130001_AddShopPhotoSourceRetention")]
public sealed class AddShopPhotoSourceRetention : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintDocuments"
                ADD COLUMN "PhotoSourceStorageKey" character varying(200) NULL,
                ADD COLUMN "PhotoSourceOriginalFileName" character varying(255) NULL,
                ADD COLUMN "PhotoSourceMediaType" character varying(100) NULL,
                ADD COLUMN "PhotoSourceSha256" character varying(64) NULL,
                ADD COLUMN "PhotoSourceSizeBytes" bigint NULL,
                ADD COLUMN "PhotoEditorSettingsJson" jsonb NULL;

            ALTER TABLE "PrintDocuments"
                ADD CONSTRAINT "CK_PrintDocuments_PhotoSourceRetention"
                CHECK (
                    (
                        "PhotoSourceStorageKey" IS NULL AND
                        "PhotoSourceOriginalFileName" IS NULL AND
                        "PhotoSourceMediaType" IS NULL AND
                        "PhotoSourceSha256" IS NULL AND
                        "PhotoSourceSizeBytes" IS NULL
                    )
                    OR
                    (
                        "PhotoSourceOriginalFileName" IS NOT NULL AND
                        length("PhotoSourceOriginalFileName") BETWEEN 1 AND 255 AND
                        "PhotoSourceMediaType" IS NOT NULL AND
                        "PhotoSourceMediaType" IN ('image/jpeg', 'image/png') AND
                        "PhotoSourceSha256" IS NOT NULL AND
                        "PhotoSourceSha256" ~ '^[0-9a-fA-F]{64}$' AND
                        "PhotoSourceSizeBytes" IS NOT NULL AND
                        "PhotoSourceSizeBytes" > 0 AND
                        (
                            (
                                "StorageMode" = 'CLOUD' AND
                                "PhotoSourceStorageKey" IS NOT NULL AND
                                length("PhotoSourceStorageKey") BETWEEN 1 AND 200
                            )
                            OR
                            (
                                "StorageMode" = 'LOCAL_AGENT' AND
                                "PhotoSourceStorageKey" IS NULL AND
                                "AgentDeviceId" IS NOT NULL
                            )
                        )
                    )
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintDocuments"
                DROP CONSTRAINT IF EXISTS "CK_PrintDocuments_PhotoSourceRetention",
                DROP COLUMN IF EXISTS "PhotoSourceStorageKey",
                DROP COLUMN IF EXISTS "PhotoSourceOriginalFileName",
                DROP COLUMN IF EXISTS "PhotoSourceMediaType",
                DROP COLUMN IF EXISTS "PhotoSourceSha256",
                DROP COLUMN IF EXISTS "PhotoSourceSizeBytes",
                DROP COLUMN IF EXISTS "PhotoEditorSettingsJson";
            """);
    }
}
