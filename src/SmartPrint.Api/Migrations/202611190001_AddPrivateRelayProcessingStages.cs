using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;

namespace SmartPrint.Api.Migrations;

[DbContext(typeof(SmartPrintDbContext))]
[Migration("202611190001_AddPrivateRelayProcessingStages")]
public sealed class AddPrivateRelayProcessingStages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PrivateRelayStage",
            table: "LocalPrintUploadTickets",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "WAITING_FOR_UPLOAD");
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PrivateRelayStageUpdatedUtc",
            table: "LocalPrintUploadTickets",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE "LocalPrintUploadTickets"
            SET "PrivateRelayStage" = 'WAITING_FOR_AGENT',
                "PrivateRelayStageUpdatedUtc" = "PrivateRelayUploadedUtc"
            WHERE "PrivateRelayUploadedUtc" IS NOT NULL
              AND "CompletedUtc" IS NULL
              AND "PrivateRelayFailedUtc" IS NULL;

            UPDATE "LocalPrintUploadTickets"
            SET "PrivateRelayStage" = 'READY',
                "PrivateRelayStageUpdatedUtc" = "CompletedUtc"
            WHERE "CompletedUtc" IS NOT NULL;

            UPDATE "LocalPrintUploadTickets"
            SET "PrivateRelayStage" = 'FAILED',
                "PrivateRelayStageUpdatedUtc" = "PrivateRelayFailedUtc"
            WHERE "PrivateRelayFailedUtc" IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PrivateRelayStageUpdatedUtc",
            table: "LocalPrintUploadTickets");
        migrationBuilder.DropColumn(
            name: "PrivateRelayStage",
            table: "LocalPrintUploadTickets");
    }
}
