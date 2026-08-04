using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopRunBaseBranchOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseBranchOverride",
                table: "LoopRuns",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseBranchOverride",
                table: "LoopRuns");
        }
    }
}
