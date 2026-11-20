using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611200001_ConfigurableVirtualPrinterTestMode")]
public sealed class ConfigurableVirtualPrinterTestMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "TreatWorkingVirtualQueuesAsPhysicalForTesting",
            table: "PlatformOperationalSettings",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "IsTestModeOverride",
            table: "PrinterDevices",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("""
            ALTER TABLE "PrinterDevices"
                DROP CONSTRAINT IF EXISTS "CK_PrinterDevices_OutputMode";

            ALTER TABLE "PrinterDevices"
                ADD CONSTRAINT "CK_PrinterDevices_OutputMode"
                CHECK ("OutputMode" IN ('PHYSICAL', 'PDF_EXPORT', 'UNSUPPORTED_VIRTUAL'));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not discard or silently reclassify the queue inventory created by
        // this migration. An older application/schema cannot safely represent it.
        migrationBuilder.Sql("""
            DO $printer_test_mode$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "PrinterDevices"
                    WHERE "OutputMode" = 'UNSUPPORTED_VIRTUAL'
                ) THEN
                    RAISE EXCEPTION 'Cannot downgrade printer test-mode migration while UNSUPPORTED_VIRTUAL printer rows exist. Preserve these rows by keeping the migration applied.';
                END IF;
            END;
            $printer_test_mode$;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE "PrinterDevices"
                DROP CONSTRAINT IF EXISTS "CK_PrinterDevices_OutputMode";

            ALTER TABLE "PrinterDevices"
                ADD CONSTRAINT "CK_PrinterDevices_OutputMode"
                CHECK ("OutputMode" IN ('PHYSICAL', 'PDF_EXPORT'));
            """);

        migrationBuilder.DropColumn(
            name: "IsTestModeOverride",
            table: "PrinterDevices");

        migrationBuilder.DropColumn(
            name: "TreatWorkingVirtualQueuesAsPhysicalForTesting",
            table: "PlatformOperationalSettings");
    }
}
