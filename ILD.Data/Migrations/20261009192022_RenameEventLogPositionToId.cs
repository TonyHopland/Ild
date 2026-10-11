using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameEventLogPositionToId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Position",
                table: "EventLogs",
                newName: "Id");

            migrationBuilder.RenameIndex(
                name: "IX_EventLogs_LoopRunId_Position",
                table: "EventLogs",
                newName: "IX_EventLogs_LoopRunId_Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Id",
                table: "EventLogs",
                newName: "Position");

            migrationBuilder.RenameIndex(
                name: "IX_EventLogs_LoopRunId_Id",
                table: "EventLogs",
                newName: "IX_EventLogs_LoopRunId_Position");
        }
    }
}
