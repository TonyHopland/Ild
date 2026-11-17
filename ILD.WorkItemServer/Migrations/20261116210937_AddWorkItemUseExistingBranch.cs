using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemUseExistingBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseExistingBranch",
                table: "WorkItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ProposedUseExistingBranch",
                table: "WorkItemEditProposals",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SnapshotUseExistingBranch",
                table: "WorkItemEditProposals",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseExistingBranch",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "ProposedUseExistingBranch",
                table: "WorkItemEditProposals");

            migrationBuilder.DropColumn(
                name: "SnapshotUseExistingBranch",
                table: "WorkItemEditProposals");
        }
    }
}
