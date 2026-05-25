using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIncomingEdgeIdToRunAndRunNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IncomingEdgeId",
                table: "LoopRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IncomingEdgeId",
                table: "LoopRunNodes",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncomingEdgeId",
                table: "LoopRuns");

            migrationBuilder.DropColumn(
                name: "IncomingEdgeId",
                table: "LoopRunNodes");
        }
    }
}
