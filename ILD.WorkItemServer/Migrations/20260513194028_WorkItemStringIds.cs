using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.WorkItemServer.Migrations
{
    /// <inheritdoc />
    public partial class WorkItemStringIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_WorkItems",
                table: "WorkItems");

            migrationBuilder.AddColumn<int>(
                name: "InternalId",
                table: "WorkItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_WorkItems",
                table: "WorkItems",
                column: "InternalId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_Id",
                table: "WorkItems",
                column: "Id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_WorkItems",
                table: "WorkItems");

            migrationBuilder.DropIndex(
                name: "IX_WorkItems_Id",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "InternalId",
                table: "WorkItems");

            migrationBuilder.AddPrimaryKey(
                name: "PK_WorkItems",
                table: "WorkItems",
                column: "Id");
        }
    }
}
