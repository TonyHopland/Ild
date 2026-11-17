using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropLoopRunLeftoverProcessWait : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChildWaitCycleCount",
                table: "LoopRuns");

            migrationBuilder.DropColumn(
                name: "ChildWaitJson",
                table: "LoopRuns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChildWaitCycleCount",
                table: "LoopRuns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ChildWaitJson",
                table: "LoopRuns",
                type: "text",
                nullable: true);
        }
    }
}
