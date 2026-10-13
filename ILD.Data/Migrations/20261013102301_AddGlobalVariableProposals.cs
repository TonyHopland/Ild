using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalVariableProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GlobalVariableProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SnapshotUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByLoopRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByRunNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByChatSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChatReplySequence = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionDeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalVariableProposals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableProposals_ChatSession_Pending",
                table: "GlobalVariableProposals",
                columns: new[] { "CreatedByChatSessionId", "NormalizedName" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableProposals_CreatedByChatSessionId",
                table: "GlobalVariableProposals",
                column: "CreatedByChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableProposals_CreatedByLoopRunId",
                table: "GlobalVariableProposals",
                column: "CreatedByLoopRunId");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableProposals_LoopRun_Pending",
                table: "GlobalVariableProposals",
                columns: new[] { "CreatedByLoopRunId", "NormalizedName" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_GlobalVariableProposals_NormalizedName",
                table: "GlobalVariableProposals",
                column: "NormalizedName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GlobalVariableProposals");
        }
    }
}
