using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopRunSessionBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoopRunSessionBindings",
                columns: table => new
                {
                    LoopRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdapterName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PlaceholderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopRunSessionBindings", x => new { x.LoopRunId, x.AdapterName, x.PlaceholderId });
                    table.ForeignKey(
                        name: "FK_LoopRunSessionBindings_LoopRuns_LoopRunId",
                        column: x => x.LoopRunId,
                        principalTable: "LoopRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoopRunSessionBindings_LoopRunId_AdapterName_SessionId",
                table: "LoopRunSessionBindings",
                columns: new[] { "LoopRunId", "AdapterName", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoopRunSessionBindings");
        }
    }
}
