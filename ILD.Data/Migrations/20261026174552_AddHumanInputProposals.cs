using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHumanInputProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HumanInputProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Output = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Text = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedByLoopRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByChatSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionDeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HumanInputProposals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HumanInputProposals_ChatSession_Pending",
                table: "HumanInputProposals",
                columns: new[] { "CreatedByChatSessionId", "WorkItemId" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_HumanInputProposals_CreatedByChatSessionId",
                table: "HumanInputProposals",
                column: "CreatedByChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_HumanInputProposals_CreatedByLoopRunId",
                table: "HumanInputProposals",
                column: "CreatedByLoopRunId");

            migrationBuilder.CreateIndex(
                name: "IX_HumanInputProposals_LoopRun_Pending",
                table: "HumanInputProposals",
                columns: new[] { "CreatedByLoopRunId", "WorkItemId" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_HumanInputProposals_RunNodeId",
                table: "HumanInputProposals",
                column: "RunNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_HumanInputProposals_WorkItemId",
                table: "HumanInputProposals",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HumanInputProposals");
        }
    }
}
