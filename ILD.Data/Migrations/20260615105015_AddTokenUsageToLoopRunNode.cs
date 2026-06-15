using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenUsageToLoopRunNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CostUsd",
                table: "LoopRunNodes",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputTokens",
                table: "LoopRunNodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokens",
                table: "LoopRunNodes",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CostUsd",
                table: "LoopRunNodes");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "LoopRunNodes");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "LoopRunNodes");
        }
    }
}
