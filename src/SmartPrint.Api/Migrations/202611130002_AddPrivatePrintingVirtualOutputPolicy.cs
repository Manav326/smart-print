using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611130002_AddPrivatePrintingVirtualOutputPolicy")]
public sealed class AddPrivatePrintingVirtualOutputPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "AllowPrivatePrintingVirtualOutput",
            table: "PlatformOperationalSettings",
            type: "boolean",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AllowPrivatePrintingVirtualOutput",
            table: "PlatformOperationalSettings");
    }
}
