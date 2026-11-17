using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611160001_AddDriverBackedNUp")]
public sealed class AddDriverBackedNUp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PagesPerSheet",
            table: "PrintJobs",
            type: "integer",
            nullable: false,
            defaultValue: 1);
        migrationBuilder.AddColumn<int>(
            name: "PagesPerSheet",
            table: "LocalPrintUploadTickets",
            type: "integer",
            nullable: false,
            defaultValue: 1);
        migrationBuilder.AddCheckConstraint(
            name: "CK_PrintJobs_PagesPerSheet",
            table: "PrintJobs",
            sql: "\"PagesPerSheet\" BETWEEN 1 AND 500");
        migrationBuilder.AddCheckConstraint(
            name: "CK_LocalPrintUploadTickets_PagesPerSheet",
            table: "LocalPrintUploadTickets",
            sql: "\"PagesPerSheet\" BETWEEN 1 AND 500");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("CK_LocalPrintUploadTickets_PagesPerSheet", "LocalPrintUploadTickets");
        migrationBuilder.DropCheckConstraint("CK_PrintJobs_PagesPerSheet", "PrintJobs");
        migrationBuilder.DropColumn(name: "PagesPerSheet", table: "LocalPrintUploadTickets");
        migrationBuilder.DropColumn(name: "PagesPerSheet", table: "PrintJobs");
    }
}
