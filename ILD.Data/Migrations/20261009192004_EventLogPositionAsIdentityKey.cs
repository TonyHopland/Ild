using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventLogPositionAsIdentityKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EventLogs_LoopRuns_LoopRunId",
                table: "EventLogs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_EventLogs",
                table: "EventLogs");

            migrationBuilder.DropIndex(
                name: "IX_EventLogs_LoopRunId_Sequence",
                table: "EventLogs");

            migrationBuilder.DropColumn(
                name: "NextEventSeq",
                table: "LoopRuns");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "EventLogs");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "EventLogs");

            migrationBuilder.AlterColumn<long>(
                name: "Position",
                table: "EventLogs",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<string>(
                name: "EdgeName",
                table: "EventLogs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_EventLogs",
                table: "EventLogs",
                column: "Position");

            migrationBuilder.CreateTable(
                name: "WorkItemStatusReasons",
                columns: table => new
                {
                    WorkItemId = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemStatusReasons", x => x.WorkItemId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoopRuns_WorkItemId_Active",
                table: "LoopRuns",
                column: "WorkItemId",
                unique: true,
                filter: "\"Status\" IN (0, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_EventLogs_LoopRunId_Position",
                table: "EventLogs",
                columns: new[] { "LoopRunId", "Position" });

            migrationBuilder.AddForeignKey(
                name: "FK_EventLogs_LoopRuns_LoopRunId",
                table: "EventLogs",
                column: "LoopRunId",
                principalTable: "LoopRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Data migration: the identity starts counting after the Ids the
            // backfill handed out, so new events sort after every existing one.
            migrationBuilder.Sql(@"
SELECT setval(pg_get_serial_sequence('""EventLogs""', 'Position'),
    (SELECT COALESCE(MAX(""Position""), 0) FROM ""EventLogs"") + 1, false);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EventLogs_LoopRuns_LoopRunId",
                table: "EventLogs");

            migrationBuilder.DropTable(
                name: "WorkItemStatusReasons");

            migrationBuilder.DropIndex(
                name: "IX_LoopRuns_WorkItemId_Active",
                table: "LoopRuns");

            migrationBuilder.DropPrimaryKey(
                name: "PK_EventLogs",
                table: "EventLogs");

            migrationBuilder.DropIndex(
                name: "IX_EventLogs_LoopRunId_Position",
                table: "EventLogs");

            migrationBuilder.DropColumn(
                name: "EdgeName",
                table: "EventLogs");

            migrationBuilder.AddColumn<int>(
                name: "NextEventSeq",
                table: "LoopRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<long>(
                name: "Position",
                table: "EventLogs",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "EventLogs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "Sequence",
                table: "EventLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_EventLogs",
                table: "EventLogs",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_EventLogs_LoopRunId_Sequence",
                table: "EventLogs",
                columns: new[] { "LoopRunId", "Sequence" });

            migrationBuilder.AddForeignKey(
                name: "FK_EventLogs_LoopRuns_LoopRunId",
                table: "EventLogs",
                column: "LoopRunId",
                principalTable: "LoopRuns",
                principalColumn: "Id");
        }
    }
}
