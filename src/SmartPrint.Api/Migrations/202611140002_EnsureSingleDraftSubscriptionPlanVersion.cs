using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611140002_EnsureSingleDraftSubscriptionPlanVersion")]
public sealed class EnsureSingleDraftSubscriptionPlanVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            WITH ranked_drafts AS (
                SELECT
                    "Id",
                    ROW_NUMBER() OVER (
                        PARTITION BY "PlanId"
                        ORDER BY "Version" DESC, "UpdatedUtc" DESC, "CreatedUtc" DESC, "Id" DESC
                    ) AS draft_rank
                FROM "SubscriptionPlanVersions"
                WHERE "Status" = 'Draft'
            )
            UPDATE "SubscriptionPlanVersions" AS version
            SET "Status" = 'Retired',
                "UpdatedUtc" = CURRENT_TIMESTAMP
            FROM ranked_drafts
            WHERE version."Id" = ranked_drafts."Id"
              AND ranked_drafts.draft_rank > 1;

            CREATE UNIQUE INDEX "IX_SubscriptionPlanVersions_PlanId_Draft"
                ON "SubscriptionPlanVersions" ("PlanId")
                WHERE "Status" = 'Draft';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_SubscriptionPlanVersions_PlanId_Draft";
            """);
    }
}