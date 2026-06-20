using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopRunPrSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrPolledEdgeStates",
                table: "LoopRuns",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrSnapshot",
                table: "LoopRuns",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrPolledEdgeStates",
                table: "LoopRuns");

            migrationBuilder.DropColumn(
                name: "PrSnapshot",
                table: "LoopRuns");
        }
    }
}
