using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611100003_AddPrivatePrintingEncryptedRelay")]
public sealed class AddPrivatePrintingEncryptedRelay : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "PrivateRelayStorageKey", table: "LocalPrintUploadTickets", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "PrivateRelayWrappedKey", table: "LocalPrintUploadTickets", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "PrivateRelayIv", table: "LocalPrintUploadTickets", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "PrivateRelayUploadedUtc", table: "LocalPrintUploadTickets", type: "timestamp with time zone", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_LocalPrintUploadTickets_PrivateRelayUploadedUtc", table: "LocalPrintUploadTickets", column: "PrivateRelayUploadedUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_LocalPrintUploadTickets_PrivateRelayUploadedUtc", table: "LocalPrintUploadTickets");
        migrationBuilder.DropColumn(name: "PrivateRelayUploadedUtc", table: "LocalPrintUploadTickets");
        migrationBuilder.DropColumn(name: "PrivateRelayIv", table: "LocalPrintUploadTickets");
        migrationBuilder.DropColumn(name: "PrivateRelayWrappedKey", table: "LocalPrintUploadTickets");
        migrationBuilder.DropColumn(name: "PrivateRelayStorageKey", table: "LocalPrintUploadTickets");
    }
}
