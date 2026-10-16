using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ILD.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoopTemplateProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoopTemplateProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoopTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseVersion = table.Column<int>(type: "integer", nullable: false),
                    Document = table.Column<string>(type: "text", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedByLoopRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByRunNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByChatSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChatReplySequence = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    EditedBeforeSave = table.Column<bool>(type: "boolean", nullable: false),
                    AppliedVersion = table.Column<int>(type: "integer", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionDeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopTemplateProposals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoopTemplateProposals_ChatSession_Pending",
                table: "LoopTemplateProposals",
                columns: new[] { "CreatedByChatSessionId", "LoopTemplateId" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_LoopTemplateProposals_CreatedByChatSessionId",
                table: "LoopTemplateProposals",
                column: "CreatedByChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopTemplateProposals_CreatedByLoopRunId",
                table: "LoopTemplateProposals",
                column: "CreatedByLoopRunId");

            migrationBuilder.CreateIndex(
                name: "IX_LoopTemplateProposals_LoopRun_Pending",
                table: "LoopTemplateProposals",
                columns: new[] { "CreatedByLoopRunId", "LoopTemplateId" },
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_LoopTemplateProposals_LoopTemplateId",
                table: "LoopTemplateProposals",
                column: "LoopTemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoopTemplateProposals");
        }
    }
}
