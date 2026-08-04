using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemBaseBranchOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseBranchOverride",
                table: "WorkItems",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseBranchOverride",
                table: "WorkItems");
        }
    }
}
