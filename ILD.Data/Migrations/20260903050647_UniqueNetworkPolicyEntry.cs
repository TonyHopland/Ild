using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueNetworkPolicyEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetworkPolicyEntries_ListKind_Host",
                table: "NetworkPolicyEntries");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkPolicyEntries_ListKind_Host_AiProviderId",
                table: "NetworkPolicyEntries",
                columns: new[] { "ListKind", "Host", "AiProviderId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetworkPolicyEntries_ListKind_Host_AiProviderId",
                table: "NetworkPolicyEntries");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkPolicyEntries_ListKind_Host",
                table: "NetworkPolicyEntries",
                columns: new[] { "ListKind", "Host" });
        }
    }
}
