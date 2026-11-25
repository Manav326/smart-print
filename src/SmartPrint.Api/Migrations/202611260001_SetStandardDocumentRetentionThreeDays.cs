using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611260001_SetStandardDocumentRetentionThreeDays")]
public sealed class SetStandardDocumentRetentionThreeDays : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<int>(
            name: "CloudCompletedDocumentRetentionDays", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 3,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 7);
        migrationBuilder.AlterColumn<int>(
            name: "AgentCompletedDocumentRetentionDays", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 3,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 7);

        migrationBuilder.Sql("""
            WITH before_settings AS MATERIALIZED (
                SELECT * FROM "PlatformOperationalSettings" WHERE "Id" = 1
            ),
            updated_settings AS (
                UPDATE "PlatformOperationalSettings"
                SET "CloudCompletedDocumentRetentionDays" = 3,
                    "AgentCompletedDocumentRetentionDays" = 3,
                    "UpdatedByUserId" = NULL,
                    "UpdatedUtc" = CURRENT_TIMESTAMP
                WHERE "Id" = 1
                RETURNING *
            )
            INSERT INTO "PlatformOperationalSettingsAudits"
                ("Id", "ChangesJson", "Reason", "ActorUserId", "CreatedUtc")
            SELECT
                gen_random_uuid(),
                jsonb_build_object(
                    'Before', jsonb_build_object(
                        'CloudCompletedDocumentRetentionDays', before."CloudCompletedDocumentRetentionDays",
                        'AgentCompletedDocumentRetentionDays', before."AgentCompletedDocumentRetentionDays",
                        'UpdatedByUserId', before."UpdatedByUserId",
                        'UpdatedUtc', before."UpdatedUtc"
                    ),
                    'After', jsonb_build_object(
                        'CloudCompletedDocumentRetentionDays', updated."CloudCompletedDocumentRetentionDays",
                        'AgentCompletedDocumentRetentionDays', updated."AgentCompletedDocumentRetentionDays",
                        'UpdatedByUserId', updated."UpdatedByUserId",
                        'UpdatedUtc', updated."UpdatedUtc"
                    )
                ),
                'System policy migration: standard completed-document retention is 72 hours (3 days).',
                NULL,
                updated."UpdatedUtc"
            FROM before_settings AS before
            CROSS JOIN updated_settings AS updated;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "PlatformOperationalSettings"
            SET "CloudCompletedDocumentRetentionDays" = 7,
                "AgentCompletedDocumentRetentionDays" = 7,
                "UpdatedByUserId" = NULL,
                "UpdatedUtc" = CURRENT_TIMESTAMP
            WHERE "Id" = 1
              AND "CloudCompletedDocumentRetentionDays" = 3
              AND "AgentCompletedDocumentRetentionDays" = 3;
            """);
        migrationBuilder.AlterColumn<int>(
            name: "CloudCompletedDocumentRetentionDays", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 7,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 3);
        migrationBuilder.AlterColumn<int>(
            name: "AgentCompletedDocumentRetentionDays", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 7,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 3);
    }
}
