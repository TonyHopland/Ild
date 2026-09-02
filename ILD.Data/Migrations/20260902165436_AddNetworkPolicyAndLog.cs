using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkPolicyAndLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetworkLogEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AiProviderId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkLogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NetworkPolicyEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ListKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AiProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkPolicyEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NetworkPolicyEntries_AiProviders_AiProviderId",
                        column: x => x.AiProviderId,
                        principalTable: "AiProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetworkLogEntries_Timestamp",
                table: "NetworkLogEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkPolicyEntries_AiProviderId",
                table: "NetworkPolicyEntries",
                column: "AiProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkPolicyEntries_ListKind_Host",
                table: "NetworkPolicyEntries",
                columns: new[] { "ListKind", "Host" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetworkLogEntries");

            migrationBuilder.DropTable(
                name: "NetworkPolicyEntries");
        }
    }
}
