using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopRunNodeTurnsModelCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CacheReadTokens",
                table: "LoopRunNodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CacheWriteTokens",
                table: "LoopRunNodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "LoopRunNodes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Turns",
                table: "LoopRunNodes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheReadTokens",
                table: "LoopRunNodes");

            migrationBuilder.DropColumn(
                name: "CacheWriteTokens",
                table: "LoopRunNodes");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "LoopRunNodes");

            migrationBuilder.DropColumn(
                name: "Turns",
                table: "LoopRunNodes");
        }
    }
}
