using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropNetworkPolicyEntryAddedForProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddedForProvider",
                table: "NetworkPolicyEntries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AddedForProvider",
                table: "NetworkPolicyEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
