using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611100004_AddLocalPrintUploadTicketPrintKind")]
public sealed class AddLocalPrintUploadTicketPrintKind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PrintKind",
            table: "LocalPrintUploadTickets",
            type: "text",
            nullable: false,
            defaultValue: "DOCUMENT");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PrintKind",
            table: "LocalPrintUploadTickets");
    }
}
