using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611170003_AddSpoolerPageProgress")]
public sealed class AddSpoolerPageProgress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "SpoolerPagesPrinted",
            table: "PrintJobs",
            type: "integer",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<int>(
            name: "SpoolerTotalPages",
            table: "PrintJobs",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SpoolerTotalPages", table: "PrintJobs");
        migrationBuilder.DropColumn(name: "SpoolerPagesPrinted", table: "PrintJobs");
    }
}
