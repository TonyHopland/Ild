using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemAttachToExistingBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AttachToExistingBranch",
                table: "WorkItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ProposedAttachToExistingBranch",
                table: "WorkItemEditProposals",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SnapshotAttachToExistingBranch",
                table: "WorkItemEditProposals",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttachToExistingBranch",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "ProposedAttachToExistingBranch",
                table: "WorkItemEditProposals");

            migrationBuilder.DropColumn(
                name: "SnapshotAttachToExistingBranch",
                table: "WorkItemEditProposals");
        }
    }
}
