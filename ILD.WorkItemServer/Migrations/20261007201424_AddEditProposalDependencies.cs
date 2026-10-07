using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEditProposalDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProposedAddDependenciesJson",
                table: "WorkItemEditProposals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedRemoveDependenciesJson",
                table: "WorkItemEditProposals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotDependenciesJson",
                table: "WorkItemEditProposals",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProposedAddDependenciesJson",
                table: "WorkItemEditProposals");

            migrationBuilder.DropColumn(
                name: "ProposedRemoveDependenciesJson",
                table: "WorkItemEditProposals");

            migrationBuilder.DropColumn(
                name: "SnapshotDependenciesJson",
                table: "WorkItemEditProposals");
        }
    }
}
