using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTagsConversationCreatedByToWorkItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConversationJson",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "WorkItems",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TagsJson",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConversationJson",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "TagsJson",
                table: "WorkItems");
        }
    }
}
