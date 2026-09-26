using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEditProposalAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChatReplySequence",
                table: "WorkItemEditProposals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByRunNodeId",
                table: "WorkItemEditProposals",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChatReplySequence",
                table: "WorkItemEditProposals");

            migrationBuilder.DropColumn(
                name: "CreatedByRunNodeId",
                table: "WorkItemEditProposals");
        }
    }
}
