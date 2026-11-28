using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611280001_AddPlatformPrinterAccessHolds")]
public sealed class AddPlatformPrinterAccessHolds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsPlatformHeld", table: "PrinterDevices",
            type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(
            name: "PlatformHoldReason", table: "PrinterDevices",
            type: "character varying(500)", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "PlatformHeldByUserId", table: "PrinterDevices",
            type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PlatformHeldUtc", table: "PrinterDevices",
            type: "timestamp with time zone", nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrinterDevices_TenantId_IsPlatformHeld",
            table: "PrinterDevices", columns: new[] { "TenantId", "IsPlatformHeld" });
        migrationBuilder.CreateIndex(
            name: "IX_PrinterDevices_PlatformHeldByUserId",
            table: "PrinterDevices", column: "PlatformHeldByUserId");
        migrationBuilder.AddCheckConstraint(
            name: "CK_PrinterDevices_PlatformHold",
            table: "PrinterDevices",
            sql: "(\"IsPlatformHeld\" = FALSE AND \"PlatformHoldReason\" IS NULL AND \"PlatformHeldByUserId\" IS NULL AND \"PlatformHeldUtc\" IS NULL) OR " +
                 "(\"IsPlatformHeld\" = TRUE AND \"PlatformHoldReason\" IS NOT NULL AND length(trim(\"PlatformHoldReason\")) >= 5 AND \"PlatformHeldByUserId\" IS NOT NULL AND \"PlatformHeldUtc\" IS NOT NULL)");

        migrationBuilder.AddForeignKey(
            name: "FK_PrinterDevices_AspNetUsers_PlatformHeldByUserId",
            table: "PrinterDevices", column: "PlatformHeldByUserId",
            principalTable: "AspNetUsers", principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.CreateTable(
            name: "PrinterPlatformAccessAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                IdentityFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterPlatformAccessAudits", x => x.Id);
                table.CheckConstraint("CK_PrinterPlatformAccessAudits_Action", "\"Action\" IN ('HOLD', 'RELEASE')");
                table.ForeignKey(
                    name: "FK_PrinterPlatformAccessAudits_Tenants_TenantId",
                    column: x => x.TenantId, principalTable: "Tenants", principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrinterPlatformAccessAudits_PrinterDevices_TenantId_PrinterDeviceId",
                    columns: x => new { x.TenantId, x.PrinterDeviceId },
                    principalTable: "PrinterDevices", principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PrinterPlatformAccessAudits_AspNetUsers_ActorUserId",
                    column: x => x.ActorUserId, principalTable: "AspNetUsers", principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex(
            name: "IX_PrinterPlatformAccessAudits_TenantId_PrinterDeviceId_CreatedUtc",
            table: "PrinterPlatformAccessAudits",
            columns: new[] { "TenantId", "PrinterDeviceId", "CreatedUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_PrinterPlatformAccessAudits_ActorUserId",
            table: "PrinterPlatformAccessAudits", column: "ActorUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // This migration is newer than the virtual-printer output-mode migration.
        // EF executes Down methods in reverse order, so refuse the downgrade here
        // before removing any columns if the next older migration cannot safely
        // roll back the visible virtual-queue inventory.
        migrationBuilder.Sql(
            """
            DO $block_platform_printer_access_downgrade$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "PrinterDevices"
                    WHERE "OutputMode" = 'UNSUPPORTED_VIRTUAL'
                ) THEN
                    RAISE EXCEPTION 'Cannot downgrade printer test-mode migration while unsupported virtual-printer inventory exists.';
                END IF;

                IF EXISTS (SELECT 1 FROM "PrinterPlatformAccessAudits") THEN
                    RAISE EXCEPTION 'Cannot downgrade printer platform access while hold audit records exist.';
                END IF;

                IF EXISTS (
                    SELECT 1 FROM "PrinterDevices"
                    WHERE "IsPlatformHeld" = TRUE
                ) THEN
                    RAISE EXCEPTION 'Cannot downgrade printer platform access while active platform holds exist.';
                END IF;
            END;
            $block_platform_printer_access_downgrade$;
            """);

        migrationBuilder.DropTable(name: "PrinterPlatformAccessAudits");
        migrationBuilder.DropCheckConstraint(
            name: "CK_PrinterDevices_PlatformHold", table: "PrinterDevices");
        migrationBuilder.DropForeignKey(
            name: "FK_PrinterDevices_AspNetUsers_PlatformHeldByUserId", table: "PrinterDevices");
        migrationBuilder.DropIndex(
            name: "IX_PrinterDevices_TenantId_IsPlatformHeld", table: "PrinterDevices");
        migrationBuilder.DropIndex(
            name: "IX_PrinterDevices_PlatformHeldByUserId", table: "PrinterDevices");
        migrationBuilder.DropColumn(name: "IsPlatformHeld", table: "PrinterDevices");
        migrationBuilder.DropColumn(name: "PlatformHoldReason", table: "PrinterDevices");
        migrationBuilder.DropColumn(name: "PlatformHeldByUserId", table: "PrinterDevices");
        migrationBuilder.DropColumn(name: "PlatformHeldUtc", table: "PrinterDevices");
    }
}
