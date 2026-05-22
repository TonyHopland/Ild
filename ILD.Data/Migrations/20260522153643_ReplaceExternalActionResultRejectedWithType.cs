using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceExternalActionResultRejectedWithType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalActionResultRejected",
                table: "LoopRuns");

            migrationBuilder.AddColumn<int>(
                name: "ExternalActionResultType",
                table: "LoopRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalActionResultType",
                table: "LoopRuns");

            migrationBuilder.AddColumn<bool>(
                name: "ExternalActionResultRejected",
                table: "LoopRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
