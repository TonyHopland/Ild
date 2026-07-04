using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemAiProviderOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiProviderOverride",
                table: "WorkItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "AiProviderOverrideId",
                table: "WorkItems",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiProviderOverride",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "AiProviderOverrideId",
                table: "WorkItems");
        }
    }
}
