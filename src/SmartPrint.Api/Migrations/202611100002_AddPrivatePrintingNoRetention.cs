using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611100002_AddPrivatePrintingNoRetention")]
public sealed class AddPrivatePrintingNoRetention : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "PrivatePrinting", table: "PrintJobs",
            type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>(
            name: "PrivatePrinting", table: "LocalPrintUploadTickets",
            type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(
            name: "DocumentEncryptionPublicKey", table: "AgentDevices",
            type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PrivateDocumentPurgeRequestedUtc", table: "PrintJobs",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PrivateDocumentPurgedUtc", table: "PrintJobs",
            type: "timestamp with time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PrivateDocumentPurgedUtc", table: "PrintJobs");
        migrationBuilder.DropColumn(name: "PrivateDocumentPurgeRequestedUtc", table: "PrintJobs");
        migrationBuilder.DropColumn(name: "DocumentEncryptionPublicKey", table: "AgentDevices");
        migrationBuilder.DropColumn(name: "PrivatePrinting", table: "LocalPrintUploadTickets");
        migrationBuilder.DropColumn(name: "PrivatePrinting", table: "PrintJobs");
    }
}