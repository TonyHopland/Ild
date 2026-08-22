using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplacePerEdgeTraversalsWithGlobalAiCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxTraversals",
                table: "LoopNodeEdges");

            migrationBuilder.AddColumn<int>(
                name: "AiTraversalCount",
                table: "LoopRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiTraversalCount",
                table: "LoopRuns");

            migrationBuilder.AddColumn<int>(
                name: "MaxTraversals",
                table: "LoopNodeEdges",
                type: "integer",
                nullable: true);
        }
    }
}
