using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalAgentGrantStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByExternalGrantId",
                table: "WorkItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByExternalGrantId",
                table: "WorkItemEditProposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemEditProposals_CreatedByExternalGrantId",
                table: "WorkItemEditProposals",
                column: "CreatedByExternalGrantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItemEditProposals_CreatedByExternalGrantId",
                table: "WorkItemEditProposals");

            migrationBuilder.DropColumn(
                name: "CreatedByExternalGrantId",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "CreatedByExternalGrantId",
                table: "WorkItemEditProposals");
        }
    }
}
