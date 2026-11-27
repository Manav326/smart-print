using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611270001_AddPrinterDiagnosticAttempts")]
public sealed class AddPrinterDiagnosticAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_Origin";

            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_Origin"
                CHECK ("Origin" IN ('CUSTOMER_QR', 'SHOP_OWNER', 'PRINTER_DIAGNOSTIC'));
            """);

        migrationBuilder.CreateTable(
            name: "PrinterDiagnosticAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PrinterDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                AgentDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                PrintJobId = table.Column<Guid>(type: "uuid", nullable: false),
                TemplateVersion = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                CapabilitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                OwnerDecision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                OwnerNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                ConfirmedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConfirmedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrinterDiagnosticAttempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_PrinterDiagnosticAttempts_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrinterDiagnosticAttempts_PrinterDevices_TenantId_PrinterDeviceId",
                    columns: x => new { x.TenantId, x.PrinterDeviceId },
                    principalTable: "PrinterDevices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PrinterDiagnosticAttempts_AgentDevices_TenantId_AgentDeviceId",
                    columns: x => new { x.TenantId, x.AgentDeviceId },
                    principalTable: "AgentDevices",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PrinterDiagnosticAttempts_PrintJobs_TenantId_PrintJobId",
                    columns: x => new { x.TenantId, x.PrintJobId },
                    principalTable: "PrintJobs",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PrinterDiagnosticAttempts_AspNetUsers_ConfirmedByUserId",
                    column: x => x.ConfirmedByUserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PrinterDiagnosticAttempts_TenantId_PrintJobId",
            table: "PrinterDiagnosticAttempts",
            columns: new[] { "TenantId", "PrintJobId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_PrinterDiagnosticAttempts_TenantId_PrinterDeviceId_CreatedUtc",
            table: "PrinterDiagnosticAttempts",
            columns: new[] { "TenantId", "PrinterDeviceId", "CreatedUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_PrinterDiagnosticAttempts_TenantId_PrinterDeviceId_ConfirmedUtc",
            table: "PrinterDiagnosticAttempts",
            columns: new[] { "TenantId", "PrinterDeviceId", "ConfirmedUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_PrinterDiagnosticAttempts_ConfirmedByUserId",
            table: "PrinterDiagnosticAttempts",
            column: "ConfirmedByUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PrinterDiagnosticAttempts");
        migrationBuilder.Sql("""
            ALTER TABLE "PrintJobs"
                DROP CONSTRAINT IF EXISTS "CK_PrintJobs_Origin";
            ALTER TABLE "PrintJobs"
                ADD CONSTRAINT "CK_PrintJobs_Origin"
                CHECK ("Origin" IN ('CUSTOMER_QR', 'SHOP_OWNER'));
            """);
    }
}
