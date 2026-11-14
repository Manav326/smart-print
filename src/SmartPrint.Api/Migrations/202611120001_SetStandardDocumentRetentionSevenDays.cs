using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611120001_SetStandardDocumentRetentionSevenDays")]
public sealed class SetStandardDocumentRetentionSevenDays : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<int>(
            name: "CloudCompletedDocumentRetentionDays", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 7,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 30);
        migrationBuilder.AlterColumn<int>(
            name: "AgentCompletedDocumentRetentionDays", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 7,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 30);
        migrationBuilder.AlterColumn<int>(
            name: "ApiRetentionSweepHours", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 1,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 24);
        migrationBuilder.AlterColumn<int>(
            name: "AgentRetentionSweepHours", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 1,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 6);

        migrationBuilder.Sql("""
            WITH before_settings AS MATERIALIZED (
                SELECT * FROM "PlatformOperationalSettings" WHERE "Id" = 1
            ),
            updated_settings AS (
                UPDATE "PlatformOperationalSettings"
                SET "CloudCompletedDocumentRetentionDays" = 7,
                    "AgentCompletedDocumentRetentionDays" = 7,
                    "ApiRetentionSweepHours" = 1,
                    "AgentRetentionSweepHours" = 1,
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
                        'TrialMaxMonthlyPrintJobs', before."TrialMaxMonthlyPrintJobs",
                        'CloudCompletedDocumentRetentionDays', before."CloudCompletedDocumentRetentionDays",
                        'AgentCompletedDocumentRetentionDays', before."AgentCompletedDocumentRetentionDays",
                        'AbandonedDocumentRetentionDays', before."AbandonedDocumentRetentionDays",
                        'PendingLocalUploadRetentionDays', before."PendingLocalUploadRetentionDays",
                        'ExpiredUploadTicketRetentionDays', before."ExpiredUploadTicketRetentionDays",
                        'EmptyCustomerSessionRetentionDays', before."EmptyCustomerSessionRetentionDays",
                        'StaleRefreshTokenRetentionDays', before."StaleRefreshTokenRetentionDays",
                        'ApiRetentionSweepHours', before."ApiRetentionSweepHours",
                        'AgentRetentionSweepHours', before."AgentRetentionSweepHours",
                        'AllowPaidJobClaimsWhenSuspended', before."AllowPaidJobClaimsWhenSuspended",
                        'UpdatedByUserId', before."UpdatedByUserId",
                        'UpdatedUtc', before."UpdatedUtc"
                    ),
                    'After', jsonb_build_object(
                        'TrialMaxMonthlyPrintJobs', updated."TrialMaxMonthlyPrintJobs",
                        'CloudCompletedDocumentRetentionDays', updated."CloudCompletedDocumentRetentionDays",
                        'AgentCompletedDocumentRetentionDays', updated."AgentCompletedDocumentRetentionDays",
                        'AbandonedDocumentRetentionDays', updated."AbandonedDocumentRetentionDays",
                        'PendingLocalUploadRetentionDays', updated."PendingLocalUploadRetentionDays",
                        'ExpiredUploadTicketRetentionDays', updated."ExpiredUploadTicketRetentionDays",
                        'EmptyCustomerSessionRetentionDays', updated."EmptyCustomerSessionRetentionDays",
                        'StaleRefreshTokenRetentionDays', updated."StaleRefreshTokenRetentionDays",
                        'ApiRetentionSweepHours', updated."ApiRetentionSweepHours",
                        'AgentRetentionSweepHours', updated."AgentRetentionSweepHours",
                        'AllowPaidJobClaimsWhenSuspended', updated."AllowPaidJobClaimsWhenSuspended",
                        'UpdatedByUserId', updated."UpdatedByUserId",
                        'UpdatedUtc', updated."UpdatedUtc"
                    )
                ),
                'System policy migration: standard completed-document retention is seven days; cleanup runs hourly by default.',
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
            SET "CloudCompletedDocumentRetentionDays" = 30,
                "AgentCompletedDocumentRetentionDays" = 30,
                "ApiRetentionSweepHours" = 24,
                "AgentRetentionSweepHours" = 6,
                "UpdatedByUserId" = NULL,
                "UpdatedUtc" = CURRENT_TIMESTAMP
            WHERE "Id" = 1
              AND "CloudCompletedDocumentRetentionDays" = 7
              AND "AgentCompletedDocumentRetentionDays" = 7
              AND "ApiRetentionSweepHours" = 1
              AND "AgentRetentionSweepHours" = 1;
            """);
        migrationBuilder.AlterColumn<int>(
            name: "CloudCompletedDocumentRetentionDays", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 30,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 7);
        migrationBuilder.AlterColumn<int>(
            name: "AgentCompletedDocumentRetentionDays", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 30,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 7);
        migrationBuilder.AlterColumn<int>(
            name: "ApiRetentionSweepHours", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 24,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 1);
        migrationBuilder.AlterColumn<int>(
            name: "AgentRetentionSweepHours", table: "PlatformOperationalSettings",
            type: "integer", nullable: false, defaultValue: 6,
            oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 1);
    }
}
