using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611260002_AddShopScopedVirtualPrinterTestGrants")]
public sealed class AddShopScopedVirtualPrinterTestGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "IdentityFingerprint",
            table: "PrinterDevices",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "VirtualPrinterTestGrants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                AgentDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                IdentityFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                RevokedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VirtualPrinterTestGrants", x => x.Id);
                table.CheckConstraint("CK_VirtualPrinterTestGrants_Expiry", "\"ExpiresUtc\" > \"CreatedUtc\"");
                table.CheckConstraint("CK_VirtualPrinterTestGrants_Revocation", "\"RevokedUtc\" IS NULL OR \"RevokedUtc\" >= \"CreatedUtc\"");
                table.ForeignKey(
                    name: "FK_VirtualPrinterTestGrants_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_VirtualPrinterTestGrants_PrinterDevices_TenantId_PrinterDeviceId",
                    columns: x => new { x.TenantId, x.PrinterDeviceId },
                    principalTable: "PrinterDevices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VirtualPrinterTestGrants_AgentDevices_TenantId_AgentDeviceId",
                    columns: x => new { x.TenantId, x.AgentDeviceId },
                    principalTable: "AgentDevices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VirtualPrinterTestGrants_AspNetUsers_ApprovedByUserId",
                    column: x => x.ApprovedByUserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_VirtualPrinterTestGrants_AspNetUsers_RevokedByUserId",
                    column: x => x.RevokedByUserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VirtualPrinterTestGrants_TenantId_PrinterDeviceId_CreatedUtc",
            table: "VirtualPrinterTestGrants",
            columns: new[] { "TenantId", "PrinterDeviceId", "CreatedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_VirtualPrinterTestGrants_TenantId_PrinterDeviceId_ExpiresUtc",
            table: "VirtualPrinterTestGrants",
            columns: new[] { "TenantId", "PrinterDeviceId", "ExpiresUtc" },
            filter: "\"RevokedUtc\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_VirtualPrinterTestGrants_TenantId_AgentDeviceId",
            table: "VirtualPrinterTestGrants",
            columns: new[] { "TenantId", "AgentDeviceId" });

        migrationBuilder.CreateIndex(
            name: "IX_VirtualPrinterTestGrants_ApprovedByUserId",
            table: "VirtualPrinterTestGrants",
            column: "ApprovedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_VirtualPrinterTestGrants_RevokedByUserId",
            table: "VirtualPrinterTestGrants",
            column: "RevokedByUserId");

        // Clear any legacy global override values before dropping the obsolete columns.
        migrationBuilder.Sql("""
            UPDATE "PlatformOperationalSettings"
            SET "TreatWorkingVirtualQueuesAsPhysicalForTesting" = FALSE
            WHERE "TreatWorkingVirtualQueuesAsPhysicalForTesting" = TRUE;

            UPDATE "PrinterDevices"
            SET "OutputMode" = 'UNSUPPORTED_VIRTUAL',
                "IsTestModeOverride" = FALSE
            WHERE "IsTestModeOverride" = TRUE;
            """);

        migrationBuilder.DropColumn(
            name: "TreatWorkingVirtualQueuesAsPhysicalForTesting",
            table: "PlatformOperationalSettings");

        migrationBuilder.DropColumn(
            name: "IsTestModeOverride",
            table: "PrinterDevices");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $virtual_printer_grants$
            BEGIN
                IF EXISTS (SELECT 1 FROM "VirtualPrinterTestGrants") THEN
                    RAISE EXCEPTION 'Cannot downgrade while virtual-printer test grant audit records exist.';
                END IF;
            END;
            $virtual_printer_grants$;
            """);
        migrationBuilder.DropTable(name: "VirtualPrinterTestGrants");
        migrationBuilder.DropColumn(name: "IdentityFingerprint", table: "PrinterDevices");
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
    }
}
