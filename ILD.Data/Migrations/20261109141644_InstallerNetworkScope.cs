using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstallerNetworkScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetworkPolicyEntries_ListKind_Host_AiProviderId",
                table: "NetworkPolicyEntries");

            migrationBuilder.AddColumn<bool>(
                name: "Installers",
                table: "NetworkPolicyEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Installer",
                table: "NetworkLogEntries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_NetworkPolicyEntries_ListKind_Host_AiProviderId_Installers",
                table: "NetworkPolicyEntries",
                columns: new[] { "ListKind", "Host", "AiProviderId", "Installers" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetworkPolicyEntries_ListKind_Host_AiProviderId_Installers",
                table: "NetworkPolicyEntries");

            migrationBuilder.DropColumn(
                name: "Installers",
                table: "NetworkPolicyEntries");

            migrationBuilder.DropColumn(
                name: "Installer",
                table: "NetworkLogEntries");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkPolicyEntries_ListKind_Host_AiProviderId",
                table: "NetworkPolicyEntries",
                columns: new[] { "ListKind", "Host", "AiProviderId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
