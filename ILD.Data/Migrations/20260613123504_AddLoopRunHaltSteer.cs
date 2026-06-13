using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopRunHaltSteer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentAiSessionId",
                table: "LoopRuns",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHalted",
                table: "LoopRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SteeringNote",
                table: "LoopRuns",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentAiSessionId",
                table: "LoopRuns");

            migrationBuilder.DropColumn(
                name: "IsHalted",
                table: "LoopRuns");

            migrationBuilder.DropColumn(
                name: "SteeringNote",
                table: "LoopRuns");
        }
    }
}
