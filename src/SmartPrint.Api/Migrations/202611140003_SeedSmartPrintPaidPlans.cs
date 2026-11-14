using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611140003_SeedSmartPrintPaidPlans")]
public sealed class SeedSmartPrintPaidPlans : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO "SubscriptionPlans"
                ("Id", "Code", "Name", "Description", "IsActive", "IsSystemPlan", "CreatedUtc", "UpdatedUtc")
            VALUES
                ('d1490000-0000-4000-8000-000000000001', 'MONTHLY_149', 'SmartPrint Monthly',
                 'Paid monthly subscription at ₹149 per month.', TRUE, FALSE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('d1499000-0000-4000-8000-000000000002', 'ANNUAL_1499', 'SmartPrint Annual',
                 'Paid annual subscription at ₹1,499 per year.', TRUE, FALSE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);

            INSERT INTO "SubscriptionPlanVersions"
                ("Id", "PlanId", "Version", "Status", "Currency", "MonthlyPriceMinor", "AnnualPriceMinor",
                 "MaxPrinters", "MaxAgentDevices", "MaxQrStations", "MaxMonthlyPrintJobs", "MaxDocumentBytes",
                 "CloudAssistedPrintingEnabled", "WhiteLabelEnabled", "AdvancedReportsEnabled",
                 "CreatedUtc", "UpdatedUtc", "PublishedUtc")
            VALUES
                ('d1490000-0000-4000-8000-000000000011', 'd1490000-0000-4000-8000-000000000001',
                 1, 'Published', 'INR', 14900, NULL, NULL, NULL, NULL, NULL, NULL,
                 TRUE, FALSE, TRUE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                ('d1499000-0000-4000-8000-000000000022', 'd1499000-0000-4000-8000-000000000002',
                 1, 'Published', 'INR', NULL, 149900, NULL, NULL, NULL, NULL, NULL,
                 TRUE, FALSE, TRUE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM "SubscriptionPlanVersions" AS version
            WHERE version."Id" IN (
                'd1490000-0000-4000-8000-000000000011',
                'd1499000-0000-4000-8000-000000000022'
            )
              AND NOT EXISTS (
                  SELECT 1 FROM "TenantSubscriptions" subscription
                  WHERE subscription."PlanVersionId" = version."Id"
              )
              AND NOT EXISTS (
                  SELECT 1 FROM "ShopSubscriptionPayments" payment
                  WHERE payment."PlanVersionId" = version."Id"
              )
              AND NOT EXISTS (
                  SELECT 1 FROM "PlatformSubscriptionAudits" audit
                  WHERE audit."PlanVersionId" = version."Id"
              );

            DELETE FROM "SubscriptionPlans" AS plan
            WHERE plan."Id" IN (
                'd1490000-0000-4000-8000-000000000001',
                'd1499000-0000-4000-8000-000000000002'
            )
              AND NOT EXISTS (
                  SELECT 1 FROM "SubscriptionPlanVersions" version
                  WHERE version."PlanId" = plan."Id"
              );
            """);
    }
}