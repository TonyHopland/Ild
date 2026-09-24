using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopRunVariableWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoopRunVariableWrites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoopRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    WrittenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopRunVariableWrites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoopRunVariableWrites_LoopRuns_LoopRunId",
                        column: x => x.LoopRunId,
                        principalTable: "LoopRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoopRunVariableWrites_LoopRunId_WrittenAt",
                table: "LoopRunVariableWrites",
                columns: new[] { "LoopRunId", "WrittenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoopRunVariableWrites");
        }
    }
}
