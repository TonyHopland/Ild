using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopNodeEdgeUserVisible : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UserVisible",
                table: "LoopNodeEdges",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserVisible",
                table: "LoopNodeEdges");
        }
    }
}
